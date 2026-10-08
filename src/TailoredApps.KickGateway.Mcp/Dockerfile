# Build context = repo root (set in workflow `context: .`).
# The MCP server has no project references — it only talks to the Api over HTTP.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TailoredApps.KickGateway.Mcp/TailoredApps.KickGateway.Mcp.csproj src/TailoredApps.KickGateway.Mcp/
RUN dotnet restore src/TailoredApps.KickGateway.Mcp/TailoredApps.KickGateway.Mcp.csproj

COPY src/TailoredApps.KickGateway.Mcp/ src/TailoredApps.KickGateway.Mcp/
RUN dotnet publish src/TailoredApps.KickGateway.Mcp/TailoredApps.KickGateway.Mcp.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Streamable HTTP transport at /mcp (stdio is the default when run locally).
ENV Mcp__Transport=http

ENTRYPOINT ["dotnet", "TailoredApps.KickGateway.Mcp.dll"]
