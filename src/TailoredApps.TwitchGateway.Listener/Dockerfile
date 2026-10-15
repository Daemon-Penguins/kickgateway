# Build context = repo root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (csproj-only first for layer caching).
COPY src/TailoredApps.Integrations.Twitch/TailoredApps.Integrations.Twitch.csproj           src/TailoredApps.Integrations.Twitch/
COPY src/TailoredApps.TwitchGateway.Contracts/TailoredApps.TwitchGateway.Contracts.csproj   src/TailoredApps.TwitchGateway.Contracts/
COPY src/TailoredApps.KickGateway.Data/TailoredApps.KickGateway.Data.csproj                 src/TailoredApps.KickGateway.Data/
COPY src/TailoredApps.KickGateway.ServiceDefaults/TailoredApps.KickGateway.ServiceDefaults.csproj src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.TwitchGateway.Listener/TailoredApps.TwitchGateway.Listener.csproj     src/TailoredApps.TwitchGateway.Listener/
RUN dotnet restore src/TailoredApps.TwitchGateway.Listener/TailoredApps.TwitchGateway.Listener.csproj

COPY src/TailoredApps.Integrations.Twitch/        src/TailoredApps.Integrations.Twitch/
COPY src/TailoredApps.TwitchGateway.Contracts/    src/TailoredApps.TwitchGateway.Contracts/
COPY src/TailoredApps.KickGateway.Data/           src/TailoredApps.KickGateway.Data/
COPY src/TailoredApps.KickGateway.ServiceDefaults/ src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.TwitchGateway.Listener/     src/TailoredApps.TwitchGateway.Listener/
RUN dotnet publish src/TailoredApps.TwitchGateway.Listener/TailoredApps.TwitchGateway.Listener.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# aspnet (not runtime) base - ServiceDefaults references Microsoft.AspNetCore.App.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TailoredApps.TwitchGateway.Listener.dll"]
