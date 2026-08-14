# Build context = repo root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (csproj-only first for layer caching).
COPY src/TailoredApps.Integrations.Kick/TailoredApps.Integrations.Kick.csproj         src/TailoredApps.Integrations.Kick/
COPY src/TailoredApps.KickGateway.Contracts/TailoredApps.KickGateway.Contracts.csproj src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.Data/TailoredApps.KickGateway.Data.csproj           src/TailoredApps.KickGateway.Data/
COPY src/TailoredApps.KickGateway.ServiceDefaults/TailoredApps.KickGateway.ServiceDefaults.csproj src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Realtime/TailoredApps.KickGateway.Realtime.csproj   src/TailoredApps.KickGateway.Realtime/
RUN dotnet restore src/TailoredApps.KickGateway.Realtime/TailoredApps.KickGateway.Realtime.csproj

COPY src/TailoredApps.Integrations.Kick/         src/TailoredApps.Integrations.Kick/
COPY src/TailoredApps.KickGateway.Contracts/     src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.Data/          src/TailoredApps.KickGateway.Data/
COPY src/TailoredApps.KickGateway.ServiceDefaults/ src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Realtime/      src/TailoredApps.KickGateway.Realtime/
RUN dotnet publish src/TailoredApps.KickGateway.Realtime/TailoredApps.KickGateway.Realtime.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# aspnet (not runtime) base — ServiceDefaults references Microsoft.AspNetCore.App.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TailoredApps.KickGateway.Realtime.dll"]
