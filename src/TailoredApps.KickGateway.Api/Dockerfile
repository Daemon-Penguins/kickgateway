# Build context = repo root (set in workflow `context: .`).
# Two-stage layer cache: copy csproj's first so `dotnet restore` survives source edits.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TailoredApps.Integrations.Kick/TailoredApps.Integrations.Kick.csproj      src/TailoredApps.Integrations.Kick/
COPY src/TailoredApps.KickGateway.Contracts/TailoredApps.KickGateway.Contracts.csproj src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/TailoredApps.KickGateway.ServiceDefaults.csproj src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Api/TailoredApps.KickGateway.Api.csproj          src/TailoredApps.KickGateway.Api/
RUN dotnet restore src/TailoredApps.KickGateway.Api/TailoredApps.KickGateway.Api.csproj

COPY src/TailoredApps.Integrations.Kick/      src/TailoredApps.Integrations.Kick/
COPY src/TailoredApps.KickGateway.Contracts/  src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/ src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Api/        src/TailoredApps.KickGateway.Api/
RUN dotnet publish src/TailoredApps.KickGateway.Api/TailoredApps.KickGateway.Api.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "TailoredApps.KickGateway.Api.dll"]
