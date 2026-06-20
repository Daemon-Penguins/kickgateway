# CLAUDE.md — kickgateway

Project-specific instructions for working with Claude in this repository. Read
this before making changes.

## What this project is

A webhook gateway for [Kick.com](https://kick.com) that ingests events from
many broadcasters under many Kick developer apps and republishes them to
RabbitMQ as typed MassTransit contracts. Downstream services subscribe via
shared contracts without ever touching the Kick API.

Status: actively developed.

## Stack (authoritative)

### Backend
- **.NET 10** + ASP.NET Core Minimal APIs
- **EF Core 10** + **SQL Server**
- **MassTransit 8** + **RabbitMQ** (cross-process bus, topic exchanges with
  broadcaster slug as routing key)
- **Blazor Server** (admin UI under `/admin`)
- **.NET Aspire** AppHost (`TailoredApps.KickGateway.AppHost`) +
  `ServiceDefaults` (OTel, health, resilience)

### Frontend
- **Blazor Server** (interactive server render). No SPA framework.

### Infrastructure
- **Docker** + Traefik (TLS) on prod
- Dev: Aspire-managed containers (SQL Server + RabbitMQ + clips-fetcher)
- Fallback dev: `docker/docker-compose.yml`

### Language conventions
- Code: English (namespace `TailoredApps`, identifiers, comments)
- README, docs/, public-facing prose: English
- Commit messages: free choice; the existing history is mixed Polish/English

## Architectural rules

- **Topic exchanges + routing key = broadcaster slug.** Every Kick contract
  is published to a topic exchange and routed by the broadcaster slug
  (lowercase). Subscribers bind to either `#` (firehose) or a specific slug.
  Helpers live in `TailoredApps.KickGateway.Contracts.KickEventTopology`.
- **Inbox + Outbox.** Webhook deliveries are deduped via the `ReceivedWebhook`
  inbox keyed on Kick's message id. Publishes go through MassTransit's
  EF-Core outbox so the inbox row + RabbitMQ publish commit together.
- **No mocks for integration tests.** Hit a real SQL Server (testcontainer).
  MassTransit's `ITestHarness` is fine for publish-flow tests.
- **No business commands in this codebase.** It is a thin dispatcher;
  consumers live in the subscriber apps (this repo's samples + any client
  apps that reference the Contracts package).
- **Clips go through a Cloudflare-bypass sidecar.** Kick clips are NOT in the
  official `public/v1` API — only on the website host
  (`kick.com/api/v2/channels/{slug}/clips`), which Cloudflare blocks by TLS
  fingerprint (a .NET `HttpClient` gets 403). The `clips-fetcher` sidecar
  (`docker/clips-fetcher`, Python + `curl_cffi`) fetches the listing with a
  browser fingerprint; all Kick URL/JSON logic stays in .NET
  (`IKickClipsClient`). Do NOT send the broadcaster OAuth token for clips — the
  endpoint is unauthenticated; the public `/obs/clips/{slug}` page is instead
  scoped to managed, enabled broadcasters. Clip video (HLS on `clips.kick.com`)
  is proxied **same-origin** (`/api/obs/hls/...`, forwarding HTTP Range) because
  the CDN sends no CORS header.
- **Channel stats use the same sidecar.** `IKickChannelClient` reads
  `kick.com/api/v2/channels/{slug}` (viewer count, live state, …) via the shared
  `IKickSidecarFetcher`. The `ChannelStatsConsumer` turns a `ChannelStatsRequested`
  message into a published `ChannelStats` (request/response also supported) — both
  contracts live in `TailoredApps.KickGateway.Contracts.Channels`.

## Testing

- Unit tests: **xUnit**.
- Integration tests must hit a real SQL Server (testcontainer or shared
  dev DB), never an in-memory provider.
- MassTransit `ITestHarness` for verifying publish flow without standing up
  RabbitMQ.
- Unit tests live in `tests/TailoredApps.KickGateway.Tests` (e.g. clip JSON
  parsing, HLS manifest rewrite). Run with `dotnet test`.

## Solution layout

```
TailoredApps.KickGateway.slnx
├── src/
│   ├── TailoredApps.Integrations.Kick/             # Kick REST + OAuth (PKCE) + signature verifier
│   ├── TailoredApps.KickGateway.Contracts/         # MassTransit contracts + topology helper (shipped as DLL/NuGet)
│   ├── TailoredApps.KickGateway.Api/               # WebAPI + Blazor admin + webhook receiver + EF
│   ├── TailoredApps.KickGateway.Worker/            # Sample subscriber (all channels, all event types)
│   ├── TailoredApps.KickGateway.Subscribers.Loyalty/    # Sample: per-channel filtered subscriber
│   ├── TailoredApps.KickGateway.Subscribers.Alerts/     # Sample: per-channel filtered subscriber
│   ├── TailoredApps.KickGateway.Subscribers.Analytics/  # Sample: per-channel filtered subscriber (one consumer, many events)
│   ├── TailoredApps.KickGateway.AppHost/           # Aspire orchestrator (F5 entrypoint)
│   └── TailoredApps.KickGateway.ServiceDefaults/   # OTel/health/resilience shared
├── docker/docker-compose.yml                       # fallback dev infra without Aspire
├── docker/clips-fetcher/                           # browser-TLS fetch proxy (clips past Cloudflare)
├── tests/TailoredApps.KickGateway.Tests/           # xUnit unit tests
└── docs/CLIENT-INTEGRATION.md                      # how external clients subscribe
```

## Secrets and configuration

- `appsettings.Development.json`, `appsettings.local.json`, `.env`, `*.user`
  are all gitignored. Use User Secrets or env vars in dev.
- `ClientId` / `ClientSecret` for each Kick developer app live in DB rows,
  entered through the Blazor admin UI. Never hardcode any specific client id.
- Bootstrap super-admin: the migration seeds a placeholder username
  (`superadmin`). On first deploy, set `Seed__SuperAdminUsername` (or the
  `SEED_SUPERADMIN_USERNAME` repo secret) to your Kick handle (lowercase).
  The placeholder is overridden once; subsequent restarts are no-ops.
- Production hostname, Docker Hub namespace, SSH target, broker credentials
  are all referenced via `${{ secrets.* }}` / `${{ vars.* }}` in
  `.github/workflows/deploy.yml`. Nothing identifying is committed.

## Dev commands

```pwsh
# Aspire (preferred) — F5 from VS on TailoredApps.KickGateway.AppHost
dotnet run --project src/TailoredApps.KickGateway.AppHost

# Manual fallback
docker compose -f docker/docker-compose.yml up -d
dotnet ef database update --project src/TailoredApps.KickGateway.Api
dotnet run --project src/TailoredApps.KickGateway.Api
dotnet run --project src/TailoredApps.KickGateway.Worker

# EF migrations
dotnet ef migrations add <Name> --project src/TailoredApps.KickGateway.Api --output-dir Data/Migrations

# Whole solution
dotnet build
```

## When to update what

- **README.md** — any change to the public-facing topology, deploy steps,
  or required secrets/vars.
- **docs/CLIENT-INTEGRATION.md** — any change to the published exchange
  topology, the `KickEventTopology` helper API, or the contracts surface.
- **CLAUDE.md (this file)** — any change to the architectural rules, stack
  decisions, or conventions above.

Avoid creating planning, decision-log, or analysis documents speculatively.
Decisions live in commit messages and `docs/` if they are durable; everything
else can stay in the conversation.
