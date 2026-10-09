# Build context = repo root.
#
# CPU-only image: the Vulkan/CUDA runtimes are shipped too, but a container only sees a GPU with the
# matching host passthrough, so UseGpu defaults to false here. Pick a model the host can keep up with
# (Small/Base on a few vCPUs; LargeV3Turbo needs a GPU or a beefy CPU). Mount /models to keep the
# downloaded GGML file between restarts and /transcripts to keep the .txt/.jsonl output.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TailoredApps.KickGateway.Contracts/TailoredApps.KickGateway.Contracts.csproj src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/TailoredApps.KickGateway.ServiceDefaults.csproj src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Subscribers.Transcriber/TailoredApps.KickGateway.Subscribers.Transcriber.csproj src/TailoredApps.KickGateway.Subscribers.Transcriber/
RUN dotnet restore src/TailoredApps.KickGateway.Subscribers.Transcriber/TailoredApps.KickGateway.Subscribers.Transcriber.csproj

COPY src/TailoredApps.KickGateway.Contracts/      src/TailoredApps.KickGateway.Contracts/
COPY src/TailoredApps.KickGateway.ServiceDefaults/ src/TailoredApps.KickGateway.ServiceDefaults/
COPY src/TailoredApps.KickGateway.Subscribers.Transcriber/ src/TailoredApps.KickGateway.Subscribers.Transcriber/
RUN dotnet publish src/TailoredApps.KickGateway.Subscribers.Transcriber/TailoredApps.KickGateway.Subscribers.Transcriber.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# aspnet (not runtime) base — ServiceDefaults references Microsoft.AspNetCore.App.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .

ENV Transcriber__ModelDir=/models \
    Transcriber__OutputDir=/transcripts \
    Transcriber__UseGpu=false
VOLUME ["/models", "/transcripts"]

ENTRYPOINT ["dotnet", "TailoredApps.KickGateway.Subscribers.Transcriber.dll"]
