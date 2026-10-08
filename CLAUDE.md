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
- **Real-time listener is a plain WebSocket, not a second bypass.** Kick's
  realtime chat/event stream is Pusher on `ws-us2.pusher.com` — a *separate origin
  with no Cloudflare*, reachable by a plain .NET `ClientWebSocket`. The
  `TailoredApps.KickGateway.Realtime` service reuses the clips-fetcher sidecar
  **only** to resolve each slug's `channel_id`/`chatroom.id` (surfaced on
  `KickChannelInfo`), then subscribes to `chatrooms.{id}.v2` + `channel.{id}` and
  maps the full `App\Events\*` catalogue to the **separate** `Contracts.Realtime.*`
  family on their own slug-routed topic exchanges (never the webhook exchanges — no
  double-publish). Unknown/renamed events fall through to `KickRealtimeUnknown`. It
  records+publishes through the **same inbox+outbox** as webhooks
  (`ReceivedRealtimeEvent` dedupe row + EF bus outbox). The Pusher app key rotates —
  it's config (`Kick:Realtime:AppKey`), not hardcoded. All Kick JSON logic stays in
  .NET (`RealtimeFrameMapper`); the WebSocket client stays dumb.
- **Realtime roster ≠ webhook roster.** The listener's roster (`RosterProvider`) is
  the **union** of two sources: explicit `RealtimeChannel` rows (managed at
  `/admin/realtime`, SuperAdmin only — slug-only, no OAuth, with per-event toggles
  `CaptureChat`/`CaptureChannel`/`VideoCaptureEnabled`) **and** enabled webhook
  broadcasters (which implicitly capture both chat+channel). A slug in both is
  followed if either source enables a given capture. Realtime needs only a public
  slug because the Pusher stream is unauthenticated, so a `RealtimeChannel` is a
  much lighter thing than a `KickBroadcasterAccount`. The listener re-reads the
  roster every `Kick:Realtime:RosterRefreshSeconds` (~30s), so panel edits take
  effect without a restart. `PusherRealtimeService` subscribes only the Pusher
  channels the merged flags select (`chatrooms.{id}.v2` iff chat, `channel.{id}`
  iff channel).
- **Live-video capture is best-effort, NOT outbox.** When
  `Kick:Realtime:Video:Enabled` is on, the listener pulls each live channel's HLS
  (plain .NET, like clips — no sidecar). Kick's live playback URL is **not** on
  `kick.com` — it's the streaming CDN (currently Amazon IVS,
  `*.playback.live-video.net`) and can change, so the HLS host guard trusts the
  playback URL's host (from Kick's channel API) and pins segment/variant fetches to
  that same host (`RealtimeHttpClients.IsFetchableHttps`/`IsSameHost`) rather than a
  hardcoded `kick.com` allowlist. It forwards raw segments as
  `Contracts.Realtime.Media.LiveVideoSegment` published **directly via `IBus`**
  (bypassing the outbox) with a short TTL, so segments are ephemeral. Per-channel
  gate `KickBroadcasterAccount.VideoCaptureEnabled` **or** `RealtimeChannel.VideoCaptureEnabled`
  (both admin UI); global switch off by default. No ffmpeg — raw HLS passthrough;
  subscribers remux.
- **Chat analytics is a derived read model, not a new ingest path.** The Api's
  `ChatProjectionService` tails both inboxes (`ReceivedWebhooks.RawBody`,
  `ReceivedRealtimeEvents.RawData`) with keyset checkpoints (`AnalyticsCheckpoints`)
  into `ChatMessages` / `ChatMentions` / `ChatterEvents`; the ingest hot paths never
  write them, and everything can be rebuilt by clearing those tables + checkpoints.
  Chat from both transports merges on Kick's message id (plus a sender+content±5 s
  guard); for channels with an enabled webhook broadcaster, webhooks are authoritative
  for non-chat events and Pusher only adds realtime-only kinds (deletions, unbans).
  All Kick JSON knowledge stays in `ChatProjectionMapper` / `RealtimeChatProjectionMapper`;
  graph/profile logic is pure (`InteractionGraph`, `Louvain`, `ChatTextStats`).
  `/api/analytics/*` is read-only and scoped per client-app role; it also accepts
  `Analytics:ApiKey` (Bearer / `X-Api-Key`) — and ONLY those endpoints do. Their queries
  run in a READ UNCOMMITTED transaction (`AnalyticsRequestFilter`) so analytics reads can
  never block or deadlock the projector / ingest writers; transient DB errors → 503. The
  MCP server (`TailoredApps.KickGateway.Mcp`) is a dumb adapter over that REST API — no
  DB access, no analysis of its own — running either locally over stdio or deployed as the
  `mcp` container (Streamable HTTP; Traefik routes `https://<host>/mcp` to it, clients use
  the same analytics key, it calls the Api at `http://api:8080`). See `docs/CHAT-ANALYTICS.md`.
- **DbContext lives in `TailoredApps.KickGateway.Data`.** `KickGatewayDbContext` +
  entities + migrations are a shared library referenced by both the Api and the
  Realtime listener (the Realtime service needs the roster + realtime inbox). The Api
  still owns migration *application* on startup. `DataProtectionDbContext` stays in
  the Api.

## Testing

- Unit tests: **xUnit**.
- Integration tests must hit a real SQL Server (testcontainer or shared
  dev DB), never an in-memory provider.
- MassTransit `ITestHarness` for verifying publish flow without standing up
  RabbitMQ.
- Unit tests live in `tests/TailoredApps.KickGateway.Tests` (e.g. clip JSON
  parsing, HLS manifest rewrite). Run with `dotnet test`.
- `ChatAnalyticsIntegrationTests` start SQL Server via Testcontainers, apply the
  real migrations, project a seeded inbox and exercise every analytics query. They
  need Docker and are reported as skipped (`[SkippableFact]`) without it.

## Solution layout

```
TailoredApps.KickGateway.slnx
├── src/
│   ├── TailoredApps.Integrations.Kick/             # Kick REST + OAuth (PKCE) + signature verifier
│   ├── TailoredApps.KickGateway.Contracts/         # MassTransit contracts + topology helper (shipped as DLL/NuGet)
│   ├── TailoredApps.KickGateway.Data/              # Shared EF DbContext + entities + migrations (Api + Realtime)
│   ├── TailoredApps.KickGateway.Api/               # WebAPI + Blazor admin + webhook receiver + EF
│   ├── TailoredApps.KickGateway.Realtime/          # Pusher realtime listener + live-video capture
│   ├── TailoredApps.KickGateway.Mcp/               # MCP server over /api/analytics — `mcp` container (/mcp) or local stdio
│   ├── TailoredApps.KickGateway.Worker/            # Sample subscriber (all channels, all event types)
│   ├── TailoredApps.KickGateway.Subscribers.Loyalty/    # Sample: per-channel filtered subscriber
│   ├── TailoredApps.KickGateway.Subscribers.Alerts/     # Sample: per-channel filtered subscriber
│   ├── TailoredApps.KickGateway.Subscribers.Analytics/  # Sample: per-channel filtered subscriber (one consumer, many events)
│   ├── TailoredApps.KickGateway.AppHost/           # Aspire orchestrator (F5 entrypoint)
│   └── TailoredApps.KickGateway.ServiceDefaults/   # OTel/health/resilience shared
├── docker/docker-compose.yml                       # fallback dev infra without Aspire
├── docker/clips-fetcher/                           # browser-TLS fetch proxy (clips past Cloudflare)
├── tests/TailoredApps.KickGateway.Tests/           # xUnit unit tests (+ SQL Server testcontainer integration tests)
├── docs/CLIENT-INTEGRATION.md                      # how external clients subscribe
└── docs/CHAT-ANALYTICS.md                          # chat analytics REST API + MCP server
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
- Dev-only local sign-in: admin auth is normally Kick OAuth SSO (needs a
  configured `IsAdminLoginClient` dev app). For local work + manual testing,
  `GET /api/auth/dev/login` mints the same admin cookie **without** Kick —
  defaults to the seeded `superadmin`, or `?username=x` (auto-provisions a
  SuperAdmin if new; existing users keep their real roles, so you can test
  restricted roles). A "Dev sign in" link appears in the admin nav.
  **Development environment only** — the endpoint is not mapped and self-guards
  (404) otherwise, and the auth cookie's `Secure` requirement is relaxed to
  `SameAsRequest` in dev so it works over plain-http localhost.
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

# EF migrations (DbContext lives in the Data project; Api is the startup project)
dotnet ef migrations add <Name> --project src/TailoredApps.KickGateway.Data --startup-project src/TailoredApps.KickGateway.Api --context KickGatewayDbContext --output-dir Migrations

# MCP server for chat analytics (stdio; normally launched by the MCP client)
dotnet run --project src/TailoredApps.KickGateway.Mcp   # needs KickGateway__BaseUrl + KickGateway__ApiKey

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
