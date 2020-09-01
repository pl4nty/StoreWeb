using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StoreWeb
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder
                       .UseStartup<Startup>()
                       .UseKestrel(options =>
                        {
                            // Get PORT for Heroku deployment
                            var port = System.Environment.GetEnvironmentVariable("PORT");
                            if (!String.IsNullOrWhiteSpace(port))
                                options.ListenAnyIP(Int32.Parse(port));
                        });
                });
    }
}
