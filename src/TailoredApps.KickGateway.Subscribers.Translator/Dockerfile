# Build context = repo root (set in workflow `context: .`).
# Translator: LiveTranscript in, LLM call, LiveTranscriptTranslation out. No database, no GPU.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TailoredApps.KickGateway.Contracts/TailoredApps.KickGateway.Contracts.csproj src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/TailoredApps.KickGateway.ServiceDefaults.csproj src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Subscribers.Translator/TailoredApps.KickGateway.Subscribers.Translator.csproj src/TailoredApps.KickGateway.Subscribers.Translator/
RUN dotnet restore src/TailoredApps.KickGateway.Subscribers.Translator/TailoredApps.KickGateway.Subscribers.Translator.csproj

COPY src/TailoredApps.KickGateway.Contracts/      src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/ src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Subscribers.Translator/ src/TailoredApps.KickGateway.Subscribers.Translator/
RUN dotnet publish src/TailoredApps.KickGateway.Subscribers.Translator/TailoredApps.KickGateway.Subscribers.Translator.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# aspnet (not runtime) base — ServiceDefaults references Microsoft.AspNetCore.App.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "TailoredApps.KickGateway.Subscribers.Translator.dll"]
