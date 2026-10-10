# Build context = repo root (set in workflow `context: .`).
# Live-subtitles site: consumes LiveTranscript from RabbitMQ, serves /{slug} (Kick player + captions) and
# /{slug}/events (SSE). No database, no video — the player is Kick's own iframe.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TailoredApps.KickGateway.Contracts/TailoredApps.KickGateway.Contracts.csproj src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/TailoredApps.KickGateway.ServiceDefaults.csproj src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Subtitles/TailoredApps.KickGateway.Subtitles.csproj src/TailoredApps.KickGateway.Subtitles/
RUN dotnet restore src/TailoredApps.KickGateway.Subtitles/TailoredApps.KickGateway.Subtitles.csproj

COPY src/TailoredApps.KickGateway.Contracts/      src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/ src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Subtitles/       src/TailoredApps.KickGateway.Subtitles/
RUN dotnet publish src/TailoredApps.KickGateway.Subtitles/TailoredApps.KickGateway.Subtitles.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "TailoredApps.KickGateway.Subtitles.dll"]
