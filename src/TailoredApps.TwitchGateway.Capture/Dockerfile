# Build context = repo root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TailoredApps.Integrations.Twitch/TailoredApps.Integrations.Twitch.csproj           src/TailoredApps.Integrations.Twitch/
COPY src/TailoredApps.TwitchGateway.Contracts/TailoredApps.TwitchGateway.Contracts.csproj   src/TailoredApps.TwitchGateway.Contracts/
COPY src/TailoredApps.KickGateway.Data/TailoredApps.KickGateway.Data.csproj                 src/TailoredApps.KickGateway.Data/
COPY src/TailoredApps.KickGateway.ServiceDefaults/TailoredApps.KickGateway.ServiceDefaults.csproj src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.TwitchGateway.Capture/TailoredApps.TwitchGateway.Capture.csproj       src/TailoredApps.TwitchGateway.Capture/
RUN dotnet restore src/TailoredApps.TwitchGateway.Capture/TailoredApps.TwitchGateway.Capture.csproj

COPY src/TailoredApps.Integrations.Twitch/        src/TailoredApps.Integrations.Twitch/
COPY src/TailoredApps.TwitchGateway.Contracts/    src/TailoredApps.TwitchGateway.Contracts/
COPY src/TailoredApps.KickGateway.Data/           src/TailoredApps.KickGateway.Data/
COPY src/TailoredApps.KickGateway.ServiceDefaults/ src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.TwitchGateway.Capture/      src/TailoredApps.TwitchGateway.Capture/
RUN dotnet publish src/TailoredApps.TwitchGateway.Capture/TailoredApps.TwitchGateway.Capture.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TailoredApps.TwitchGateway.Capture.dll"]
