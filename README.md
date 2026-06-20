# Kick Gateway

Webhook gateway that ingests [Kick.com](https://kick.com) events for many
broadcasters under many Kick developer apps and republishes them to RabbitMQ
via MassTransit. Downstream services subscribe to typed contracts in
`TailoredApps.KickGateway.Contracts` without ever touching the Kick API.

Per-channel filtering is built in: each downstream subscriber binds its queues
to one or more broadcaster slugs (or `#` for the firehose), so the broker
filters at delivery time and the consumer never sees messages from channels it
doesn't care about. See `docs/CLIENT-INTEGRATION.md`.

## What's in the box

```
src/
  TailoredApps.Integrations.Kick/             # Kick REST + OAuth (PKCE) + sig-verify client.
  TailoredApps.KickGateway.Contracts/         # MassTransit message contracts + topology helper (shared lib).
  TailoredApps.KickGateway.Api/               # WebAPI + Blazor admin + webhook receiver. Dockerfile here.
  TailoredApps.KickGateway.Worker/            # Sample subscriber, all channels (logs every contract). Dockerfile here.
  TailoredApps.KickGateway.Subscribers.*/     # Three sample apps demonstrating per-channel filtering.
  TailoredApps.KickGateway.AppHost/           # .NET Aspire orchestrator (F5 from VS).
  TailoredApps.KickGateway.ServiceDefaults/   # OTel + health + resilience.
docker/docker-compose.yml                     # RabbitMQ + SQL Server (dev fallback).
docker/clips-fetcher/                         # Browser-TLS fetch proxy (reads clips past Cloudflare).
docs/CLIENT-INTEGRATION.md                    # How to write a downstream subscriber.
.github/workflows/deploy.yml                  # Build → push Docker images → deploy via SSH.
```

## Architecture

- **Multi-client.** Each row in `KickClientApp` holds one Kick developer app
  (ClientId / Secret / RedirectUri / WebhookUrl). The gateway can host any
  number of them. Each Kick app must have its webhook URL pointed at this
  gateway's `/api/webhooks/kick`.
- **Multi-broadcaster.** Each row in `KickBroadcasterAccount` is one channel
  authenticated under one client app. Same channel under two client apps =
  two rows. Tokens are refreshed on demand and on a 2-minute background pass.
- **Inbox.** Every webhook delivery is inserted into `ReceivedWebhook` keyed
  on Kick's `Kick-Event-Message-Id`. Duplicates are short-circuited.
- **Outbox.** The publish to RabbitMQ goes through MassTransit's
  `AddEntityFrameworkOutbox<KickGatewayDbContext>` — the message only
  leaves the DB once the inbox row commits. No publish-without-record drift
  and vice-versa.
- **Topology.** One **topic** exchange per published contract; the
  broadcaster slug is the routing key. Each subscriber binds its durable
  queue to the channels it cares about (or `#` for all). Workers can scale
  horizontally; consumers on the same queue compete.

## Quick start

### Option A — .NET Aspire (recommended, F5 from Visual Studio)

Open `TailoredApps.KickGateway.slnx`, set **TailoredApps.KickGateway.AppHost**
as the startup project, and F5. The AppHost:

- starts SQL Server + RabbitMQ in containers (with the management UI exposed)
- injects `ConnectionStrings:KickGateway` into the Api and `RabbitMq:*` into
  every subscriber
- runs the Api + Worker + three sample subscribers side-by-side
- opens the Aspire dashboard (resources, logs, traces, metrics)

From the CLI:

```pwsh
dotnet run --project src/TailoredApps.KickGateway.AppHost
```

### Option B — manual

```pwsh
# 1. Boot infra
docker compose -f docker/docker-compose.yml up -d

# 2. Apply schema (runs on first Api start automatically), or manually:
dotnet ef database update --project src/TailoredApps.KickGateway.Api

# 3. Run gateway + worker (and any sample subscriber you like)
dotnet run --project src/TailoredApps.KickGateway.Api
dotnet run --project src/TailoredApps.KickGateway.Worker
```

Open `https://localhost:5001/admin`:

1. **Clients** — paste ClientId / Secret / RedirectUri (the one set in the
   Kick dev portal) plus the webhook URL the portal will POST to.
2. **Broadcasters** — pick a client app, click **Start OAuth**. The Kick
   consent screen appears; on approval you're redirected back and a row
   appears here.
3. Click **Enroll all events** to subscribe the broadcaster to every event
   in `KickEventTypes.All` via `/public/v1/events/subscriptions`.

Webhook deliveries hit `/api/webhooks/kick`, get signature-verified, deduped,
mapped to a typed contract, and published. The sample worker logs everything;
your real subscribers can be in any process that references the contracts
project.

## OBS clips player

Every onboarded broadcaster gets a public, no-login page that auto-plays its
Kick **clips** — drop it straight into OBS as a *Browser* source:

```
https://${PUBLIC_HOST}/obs/clips/{slug}
```

Each channel's playback is configured in the admin **Broadcasters** page
(**Settings**): the **order** (`latest` or `most viewed`, with a time window —
day / week / month / all — for most-viewed), whether to **shuffle**, and how many
top clips to **play first** before shuffling. The same row has the URL
(**Copy URL** / **Preview**) and a **Show/Hide** toggle
(`KickBroadcasterAccount.ClipsDisplayEnabled`). Defaults match the original
behaviour: two newest first, then random forever.

Optional player query params (display only — order is admin-controlled): `?muted=1`,
`?overlay=1` (title/creator lower-third), `?refresh=N` (minutes between background
list refreshes).

### Why a sidecar (Cloudflare)

Clips aren't in the official `api.kick.com/public/v1` API — they only exist on
the website host (`kick.com/api/v2/channels/{slug}/clips`), which is behind
**Cloudflare TLS-fingerprint blocking** (a plain .NET `HttpClient` gets 403).
The **`clips-fetcher`** sidecar (`docker/clips-fetcher`, Python + `curl_cffi`)
fetches that listing with a real browser TLS fingerprint; the gateway owns all
Kick logic and just calls it (`IKickClipsClient`). Clip **video** (HLS on
`clips.kick.com`) is reachable directly but sends no CORS header, so the gateway
proxies the manifest + segments same-origin (`/api/obs/hls/...`, forwarding HTTP
Range requests). Clip lists are cached (`Kick:ClipsCacheMinutes`, default 10) to
bound Cloudflare hits.

Aspire builds and wires the sidecar automatically (with a per-run shared secret).
For the manual fallback, `docker compose` runs it on `localhost:8099`; see
`docker/clips-fetcher/README.md`.

## Deploy

`.github/workflows/deploy.yml` builds three Docker images (api, worker,
clips-fetcher), pushes to Docker Hub,
and deploys to a VPS via SSH + `docker compose`. The compose stack joins two
shared external Docker networks:

- `traefik-public` — Traefik handles TLS for `${PUBLIC_HOST}`
- `db-internal`   — reaches the shared SQL Server

### Required GitHub repo configuration

**Variables** (Settings → Secrets and variables → Actions → Variables):

| Variable | What |
| --- | --- |
| `DOCKERHUB_NAMESPACE` | Docker Hub user/org under which images are pushed (e.g. `myorg`) |

**Secrets** (Settings → Secrets and variables → Actions → Secrets):

| Secret | What |
| --- | --- |
| `DOCKERHUB_USER` / `DOCKERHUB_TOKEN` | push to Docker Hub |
| `DEPLOY_HOST` / `DEPLOY_USER` / `DEPLOY_SSH_KEY` | SSH into the VPS |
| `PUBLIC_HOST` | Fully qualified hostname for the public URL (`example.com`) |
| `KICK_WEBHOOK_URL` | Full webhook URL Kick will POST to (`https://example.com/api/webhooks/kick`) |
| `DB_CONNECTION_STRING` | `Server=…,1433;Database=kickgateway;User Id=…;Password=…;TrustServerCertificate=true;Encrypt=false` |
| `RABBITMQ_HOST` / `RABBITMQ_PORT` / `RABBITMQ_VHOST` / `RABBITMQ_USERNAME` / `RABBITMQ_PASSWORD` | broker (private vhost recommended) |
| `SEED_SUPERADMIN_USERNAME` | Kick handle (lowercase) of the first super-admin. Used only on first deploy; ignored thereafter. |
| `CLIPS_FETCHER_SECRET` | Shared secret between the API and the clips-fetcher sidecar (any random string). |

### Pre-deploy operator checklist

1. DNS A record for `${PUBLIC_HOST}` → VPS public IP.
2. `kickgateway` database created on the SQL Server + a user with `db_owner` on it. EF migrations apply on first Api start.
3. Kick dev portal: each `KickClientApp` row's RedirectUri set to `https://${PUBLIC_HOST}/api/auth/kick/callback`, WebhookUrl set to `https://${PUBLIC_HOST}/api/webhooks/kick`.
4. RabbitMQ broker is up and credentials are available.

## Consuming the contracts package

The `TailoredApps.KickGateway.Contracts` package is published to **GitHub
Packages** on every push to main that touches the contracts project. Version
scheme: `0.2.<github-run-number>` — strictly monotonic. Floating on `0.2.*`
always pulls the newest.

GitHub Packages requires auth even for public repos. Create a Personal Access
Token (classic) with `read:packages` scope, then add a `nuget.config` next to
your `.csproj`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="github-daemon-penguins"
         value="https://nuget.pkg.github.com/Daemon-Penguins/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github-daemon-penguins>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_PAT%" />
    </github-daemon-penguins>
  </packageSourceCredentials>
</configuration>
```

Then in your project:

```bash
dotnet add package TailoredApps.KickGateway.Contracts --version "0.2.*"
```

Don't commit a `nuget.config` containing a real token — use an env var
(`%GITHUB_PACKAGES_PAT%` above) or `dotnet nuget update source ... --username
... --password ... --store-password-in-clear-text` for CI.

## Wiring a downstream subscriber

See `docs/CLIENT-INTEGRATION.md` for the full guide. The short version:

```csharp
services.AddMassTransit(x =>
{
    x.AddConsumer<MyChatConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host("localhost", "/", h => { h.Username("guest"); h.Password("guest"); });

        // Match the gateway's topology (topic exchanges per type).
        KickEventTopology.ConfigurePublishTopology(cfg);

        cfg.ReceiveEndpoint("myapp-chat", e =>
        {
            // Bind only to messages where BroadcasterSlug == "xqc".
            KickEventTopology.BindKickEvent<ChatMessageSent>(e, "xqc");
            e.ConfigureConsumer<MyChatConsumer>(ctx);
        });
    });
});

public class MyChatConsumer : IConsumer<ChatMessageSent>
{
    public Task Consume(ConsumeContext<ChatMessageSent> ctx) => /* … */;
}
```

### On-demand channel stats

Subscribers can also ask the gateway for a channel's **live stats** (viewer
count, live state, followers, category, …) by publishing `ChannelStatsRequested`
and consuming `ChannelStats` — or via `IRequestClient` for inline request/
response. The gateway fetches them from the Cloudflare-protected website API
through the clips-fetcher sidecar. See `docs/CLIENT-INTEGRATION.md`.

## Notes

- Kick's `events[].version` MUST be a JSON number on subscription create.
  `KickClient.CreateSubscriptionAsync` already does that.
- `broadcaster_user_id` is omitted when subscribing under a user token —
  Kick infers from the token. Required only for app tokens.
- The webhook URL Kick POSTs to is the value in the dev portal's app
  config, NOT what you send in the subscription body.
- Public key rotates; `KickSignatureVerifier` retries once with a forced
  refresh on verification failure.

## License

MIT.
