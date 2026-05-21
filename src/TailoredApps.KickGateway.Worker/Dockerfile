# Build context = repo root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TailoredApps.KickGateway.Contracts/TailoredApps.KickGateway.Contracts.csproj src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/TailoredApps.KickGateway.ServiceDefaults.csproj src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Worker/TailoredApps.KickGateway.Worker.csproj      src/TailoredApps.KickGateway.Worker/
RUN dotnet restore src/TailoredApps.KickGateway.Worker/TailoredApps.KickGateway.Worker.csproj

COPY src/TailoredApps.KickGateway.Contracts/  src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/ src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Worker/     src/TailoredApps.KickGateway.Worker/
RUN dotnet publish src/TailoredApps.KickGateway.Worker/TailoredApps.KickGateway.Worker.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# aspnet (not runtime) base — ServiceDefaults references Microsoft.AspNetCore.App
# (health checks, hosting abstractions). The Worker doesn't expose HTTP, but the
# shared AddServiceDefaults() call still needs the AspNetCore framework on disk.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TailoredApps.KickGateway.Worker.dll"]
