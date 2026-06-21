# Build on the native platform to avoid emulating the SDK under QEMU; the
# published output is framework-dependent (portable) so it runs on any target arch.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build-env
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY app/*.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY ./app/ ./
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build-env /app/out .
# ENTRYPOINT ["dotnet", "StoreWeb.dll"]
# heroku uses the following
CMD ASPNETCORE_URLS=http://*:80 dotnet StoreWeb.dll

EXPOSE 80
