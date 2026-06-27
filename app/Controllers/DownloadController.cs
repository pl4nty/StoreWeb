using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using StoreLib.Models;
using StoreLib.Services;
using StoreWeb.Models;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net.Mime;

namespace StoreWeb.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DownloadController : ControllerBase
    {
        private static readonly MSHttpClient _httpClient = new MSHttpClient();

        // GET: api/Download
        // Accepts the same parameters as api/Packages, but instead of returning data
        // it issues a temporary redirect to the primary install file (e.g. appxbundle),
        // ignoring framework dependencies.
        //
        // Dependencies can't be queried from the catalog on their own, so to grab one
        // (e.g. a framework like Microsoft.NET.Native.Framework.2.2) pass Name (and
        // optionally Arch) to redirect to that specific package out of the product's
        // package list instead of the primary install file.
        [HttpGet]
        public async Task<IActionResult> GetDownload(
            /*Mandatory get parameter*/ string Id,
            string Idtype = "url",
            string Environment = "Production",
            string Market = "US",
            string Lang = "en",
            string Msatoken = null,
            string Name = null,
            string Arch = null)
        {
            Packages packagerequest = new Packages()
            {
                id = Id,
                environment = (DCatEndpoint)Enum.Parse(typeof(DCatEndpoint), Environment),
                lang = (Lang)Enum.Parse(typeof(Lang), Lang),
                market = (Market)Enum.Parse(typeof(Market), Market),
                msatoken = Msatoken
            };
            switch (Idtype)
            {
                case "url":
                    packagerequest.id = new Regex(@"[a-zA-Z0-9]{12}").Matches(packagerequest.id)[0].Value;
                    packagerequest.type = IdentiferType.ProductID;
                    break;
                case "productid":
                    packagerequest.type = IdentiferType.ProductID;
                    break;
                case "pfn":
                    packagerequest.type = IdentiferType.PackageFamilyName;
                    break;
                case "cid":
                    packagerequest.type = IdentiferType.ContentID;
                    break;
                case "xti":
                    packagerequest.type = IdentiferType.XboxTitleID;
                    break;
                case "lxpi":
                    packagerequest.type = IdentiferType.LegacyXboxProductID;
                    break;
                case "lwspi":
                    packagerequest.type = IdentiferType.LegacyWindowsStoreProductID;
                    break;
                case "lwppi":
                    packagerequest.type = IdentiferType.LegacyWindowsPhoneProductID;
                    break;
                default:
                    packagerequest.type = (IdentiferType)Enum.Parse(typeof(IdentiferType), Idtype);
                    break;
            }
            DisplayCatalogHandler dcat = new DisplayCatalogHandler(packagerequest.environment, new Locale(packagerequest.market, packagerequest.lang, true));
            if (!string.IsNullOrWhiteSpace(packagerequest.msatoken))
            {
                await dcat.QueryDCATAsync(packagerequest.id, packagerequest.type, packagerequest.msatoken);
            }
            else
            {
                await dcat.QueryDCATAsync(packagerequest.id, packagerequest.type);
            }
            var productpackages = await dcat.GetPackagesForProductAsync();

            // When a Name (and optionally Arch) is supplied, redirect to that specific package
            // out of the full list rather than the primary install file. This is how a single
            // dependency (e.g. a framework) is downloaded, since dependencies can't be queried
            // from the catalog independently of the product that pulls them in.
            if (!string.IsNullOrWhiteSpace(Name))
            {
                PackageInstance dependency = productpackages.FirstOrDefault(package =>
                    MonikerMatches(package.PackageMoniker, Name, Arch));
                if (dependency == null)
                {
                    return NotFound();
                }
                return Redirect(dependency.PackageUri.ToString());
            }

            var mainPackage = dcat.ProductListing.Product.DisplaySkuAvailabilities[0].Sku.Properties.Packages[0];

            // The main package's family name (e.g. Microsoft.WindowsCalculator_8wekyb3d8bbwe) lets us
            // pick the primary install file out of the package list while skipping framework dependencies,
            // which share the publisher id but have a different package name.
            List<PackageInstance> candidates = new List<PackageInstance>();
            string familyName = mainPackage.PackageFamilyName;
            if (!string.IsNullOrEmpty(familyName) && familyName.Contains('_'))
            {
                int split = familyName.LastIndexOf('_');
                string name = familyName.Substring(0, split);
                string publisher = familyName.Substring(split + 1);

                candidates = productpackages.Where(package =>
                    package.PackageMoniker.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) &&
                    package.PackageMoniker.EndsWith("_" + publisher, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // The same package is usually offered both unencrypted (.appxbundle) and encrypted
            // (.eappxbundle) under an identical moniker, so the file extension is only visible on
            // the download itself. Resolve each candidate's real file name and prefer the
            // unencrypted bundle, which is the one that can actually be installed directly.
            Uri downloadUri = null;
            int bestRank = int.MinValue;
            foreach (PackageInstance candidate in candidates)
            {
                int rank = RankInstallFile(await GetDownloadFileNameAsync(candidate.PackageUri));
                if (rank > bestRank)
                {
                    bestRank = rank;
                    downloadUri = candidate.PackageUri;
                }
            }

            if (downloadUri == null && mainPackage.PackageDownloadUris != null && mainPackage.PackageDownloadUris.Count > 0)
            {
                downloadUri = new Uri(mainPackage.PackageDownloadUris[0].Uri);
            }

            if (downloadUri == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrEmpty(mainPackage.Version))
            {
                Response.Headers["x-ms-meta-version"] = mainPackage.Version;
            }
            return Redirect(downloadUri.ToString());
        }

        // Matches a package moniker against a requested name and (optionally) architecture.
        // Monikers look like "Microsoft.NET.Native.Framework.2.2_2.2.29512.0_x64__8wekyb3d8bbwe",
        // i.e. name_version_arch__publisher, so we split on '_' and compare the relevant parts.
        private static bool MonikerMatches(string moniker, string name, string arch)
        {
            if (string.IsNullOrEmpty(moniker))
            {
                return false;
            }
            string[] parts = moniker.Split('_');
            if (parts.Length < 3)
            {
                return false;
            }
            string monikerName = parts[0];
            string monikerArch = parts[2];
            if (!monikerName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(arch) &&
                !monikerArch.Equals(arch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        // Resolves the actual file name a download URL serves via its Content-Disposition header.
        private static async Task<string> GetDownloadFileNameAsync(Uri uri)
        {
            HttpRequestMessage httpRequest = new HttpRequestMessage();
            httpRequest.RequestUri = uri;
            httpRequest.Method = HttpMethod.Head;
            httpRequest.Headers.Add("Connection", "Keep-Alive");
            httpRequest.Headers.Add("Accept", "*/*");
            httpRequest.Headers.Add("User-Agent", "Microsoft-Delivery-Optimization/10.0");
            HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, new System.Threading.CancellationToken());
            IEnumerable<string> values;
            if (httpResponse.Content.Headers.TryGetValues("Content-Disposition", out values))
            {
                return new ContentDisposition(values.First()).FileName;
            }
            return null;
        }

        // Ranks an install file so the primary, unencrypted bundle is preferred over an
        // encrypted (.e*) variant or a non-bundle single package.
        private static int RankInstallFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return 0;
            }
            string name = fileName.ToLowerInvariant();
            if (name.EndsWith(".appxbundle") || name.EndsWith(".msixbundle")) return 4;
            if (name.EndsWith(".appx") || name.EndsWith(".msix")) return 3;
            if (name.EndsWith(".eappxbundle") || name.EndsWith(".emsixbundle")) return 2;
            if (name.EndsWith(".eappx") || name.EndsWith(".emsix")) return 1;
            return 0;
        }
    }
}
