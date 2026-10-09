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
  TailoredApps.KickGateway.Data/              # Shared EF Core DbContext + entities + migrations (Api + Realtime).
  TailoredApps.KickGateway.Api/               # WebAPI + Blazor admin + webhook receiver. Dockerfile here.
  TailoredApps.KickGateway.Realtime/          # Real-time Pusher listener + live-video capture. Dockerfile here.
  TailoredApps.KickGateway.Mcp/               # MCP server over the chat-analytics API — `mcp` container (/mcp) or local stdio. Dockerfile here.
  TailoredApps.KickGateway.Worker/            # Sample subscriber, all channels (logs every contract). Dockerfile here.
  TailoredApps.KickGateway.Subscribers.*/     # Sample apps demonstrating per-channel filtering (+ VideoRecorder).
  TailoredApps.KickGateway.Subscribers.Transcriber/ # Live-stream speech-to-text (ffmpeg + Whisper) -> LiveTranscript. Dockerfile here.
  TailoredApps.KickGateway.AppHost/           # .NET Aspire orchestrator (F5 from VS).
  TailoredApps.KickGateway.ServiceDefaults/   # OTel + health + resilience.
docker/docker-compose.yml                     # RabbitMQ + SQL Server (dev fallback).
docker/clips-fetcher/                         # Browser-TLS fetch proxy (reads clips past Cloudflare).
docs/CLIENT-INTEGRATION.md                    # How to write a downstream subscriber.
docs/CHAT-ANALYTICS.md                        # Chatter profiles + interaction graph (REST + MCP).
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
- **Real-time listener.** The `Realtime` service subscribes to Kick's
  *unofficial* Pusher WebSocket (`ws-us2.pusher.com` — a separate origin, not
  Cloudflare-gated) and republishes the much richer event catalogue (deletions,
  pins, polls, chatroom modes, host/raid, follower ticks, …) as a **separate**
  `Contracts.Realtime.*` family on their own slug-routed exchanges. It reuses
  the clips-fetcher sidecar only to resolve each slug's `channel_id`/`chatroom_id`,
  and records+publishes with the same inbox+outbox durability as the webhook path.
  Best-effort/unsigned — a complement to the signed webhook stream, not a
  replacement. Its roster is the **union** of the channels added on the
  **Realtime** admin page (`/admin/realtime` — slug-only, no OAuth, with
  per-event capture toggles) and your enabled webhook broadcasters.
- **Live-video capture (opt-in).** When `Kick:Realtime:Video:Enabled` is on,
  the listener also pulls each live channel's HLS stream and forwards the raw
  segments (`Contracts.Realtime.Media.LiveVideoSegment`) — published directly
  (bypassing the outbox) with a short TTL, so subscribers can store the video
  themselves. Off by default; heavy. Per-channel toggle in the admin UI.
- **Live transcription.** `Subscribers.Transcriber` consumes that video firehose,
  pulls the audio track out with a long-lived ffmpeg per channel (16 kHz mono PCM,
  no video decoding), cuts it on pauses into ~15 s chunks and runs them through
  Whisper (whisper.cpp via Whisper.net — GPU through Vulkan/CUDA when present, CPU
  otherwise; model downloaded on first run). Each chunk is published as
  `Contracts.Realtime.Media.LiveTranscript` on its own slug-routed exchange and
  appended to per-channel daily `.txt`/`.jsonl` files. Needs `ffmpeg` on PATH;
  when Whisper can't keep up it drops the oldest pending audio to stay live. GPU
  faults are survived (watchdog + processor/model rebuild + CPU fallback); on Intel
  Arc the Vulkan cooperative-matrix shaders are disabled by default because they
  crash after a few minutes (`Transcriber:DisableVulkanCoopmat`). Profanity is
  transcribed verbatim (`Transcriber:Uncensored`), Whisper's own hallucinations
  (credits, loops, sound tags) are filtered.
- **Chat analytics.** The Api projects both inboxes (`ReceivedWebhooks.RawBody`
  + `ReceivedRealtimeEvents.RawData`) into a queryable read model (`ChatMessages`,
  `ChatMentions`, `ChatterEvents`) and serves read-only chatter profiles, pair
  dynamics and a who-talks-to-whom graph under `/api/analytics/*` (admin cookie or
  `Analytics:ApiKey`). `TailoredApps.KickGateway.Mcp` exposes those as MCP tools
  for Claude & co. It is deployed as the `mcp` container at `https://${PUBLIC_HOST}/mcp`
  (Bearer = the analytics key) and can also run locally over stdio. See
  `docs/CHAT-ANALYTICS.md`.

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
4. **Realtime** (SuperAdmin) — add channels for the real-time listener by
   **slug** (no OAuth needed). Toggle chat / channel / video capture per
   channel and **Verify** a slug to resolve its ids + live status. This roster
   is unioned with your enabled webhook broadcasters.

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

`.github/workflows/deploy.yml` builds the Docker images (api, worker, realtime,
mcp, transcriber, clips-fetcher), pushes to Docker Hub,
and deploys to a VPS via SSH + `docker compose`. The compose stack joins two
shared external Docker networks:

- `traefik-public` — Traefik handles TLS for `${PUBLIC_HOST}`
- `db-internal`   — reaches the shared SQL Server

### Image tags & rollback

Every build pushes each image under **two** tags: `:latest` (moving pointer) and
`:<commit-sha>` (immutable). The deployed compose pins to the commit SHA via an
`IMAGE_TAG` written into the server's `.env`, so the running version is always an
exact, reproducible image — not "whatever `latest` happens to be".

To roll back, point `IMAGE_TAG` at an earlier commit's image (still in Docker Hub)
and re-up — no rebuild needed:

```bash
ssh <deploy-user>@<host>
cd /opt/apps/kickgateway
sed -i 's/^IMAGE_TAG=.*/IMAGE_TAG=<previous-commit-sha>/' .env
docker compose pull && docker compose up -d
```

The next pipeline deploy overwrites `IMAGE_TAG` with its own SHA, so re-running the
`Build & Deploy` workflow (via `workflow_dispatch` on the desired commit, or a new
push) returns to the pipeline-managed version.

### Required GitHub repo configuration

**Variables** (Settings → Secrets and variables → Actions → Variables):

| Variable | What |
| --- | --- |
| `DOCKERHUB_NAMESPACE` | Docker Hub user/org under which images are pushed (e.g. `myorg`) |
| `REALTIME_VIDEO_ENABLED` | Optional. `true` turns on the realtime listener's live-video capture (heavy). Defaults to `false`. The `transcriber` container only has work while this is on. |
| `TRANSCRIBER_MODEL` | Optional. Whisper GGML model for the CPU-only `transcriber` container: `Small` (default), `Base`, `Medium`, `LargeV3Turbo` (needs a strong CPU; ~6x the cost of `Small`). |
| `TRANSCRIBER_QUANTIZATION` | Optional. `Q5_0` (default), `Q8_0` or `NoQuantization`. |
| `TRANSCRIBER_LANGUAGE` | Optional. Whisper language code, default `pl`; `auto` detects per chunk. |
| `TRANSCRIBER_THREADS` | Optional. Whisper CPU threads, default `0` = auto (cores minus 2, clamped 4..16). |
| `TRANSCRIBER_WRITE_FILES` | Optional. `true` also appends per-channel `.txt`/`.jsonl` files into the `transcriber-transcripts` volume. Default `false` - the api's `LiveTranscripts` table is the record. |

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
| `ANALYTICS_API_KEY` | Optional. Enables API-key access to `/api/analytics/*` and is the key for the `mcp` container at `/mcp`. Random, ≥ 24 chars. Empty = admin cookie only, and `/mcp` rejects everything. |

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

### Live transcripts

If the transcriber is running, subscribe to its output the same way — small
messages, no TTL, so a normal durable queue is fine:

```csharp
KickMediaTopology.ConfigurePublishTopology(cfg);
cfg.ReceiveEndpoint("myapp-transcripts", e =>
{
    KickMediaTopology.BindLiveTranscript(e, "xqc"); // or no slugs for every channel
    e.ConfigureConsumer<MyTranscriptConsumer>(ctx);
});
```

The Api stores every transcript in the `LiveTranscripts` table and serves them under
`/api/analytics/channels/{slug}/transcripts` (and `/transcripts/at?at=...` for "what was
being said when this chat message was sent"); see `docs/CHAT-ANALYTICS.md`.

Running the transcriber itself (Aspire starts it for you; standalone):

```pwsh
# ffmpeg must be on PATH (or set Transcriber__FfmpegPath). First run downloads the model.
dotnet run --project src/TailoredApps.KickGateway.Subscribers.Transcriber
# Channels, language, model, GPU, output dir — see the "Transcriber" section in its appsettings.json,
# e.g. Transcriber__Channels__0=xqc Transcriber__Language=en Transcriber__Model=Small Transcriber__UseGpu=false
```

On the VPS `deploy.yml` runs it as the `transcriber` compose service (CPU only, model via
`TRANSCRIBER_MODEL`, GGML cached in the `transcriber-models` volume). By hand:

```sh
docker build -f src/TailoredApps.KickGateway.Subscribers.Transcriber/Dockerfile -t kickgateway-transcriber .
docker run -d --name transcriber -v whisper-models:/models -v transcripts:/transcripts \
  -e RabbitMq__Host=rabbitmq -e RabbitMq__Username=... -e RabbitMq__Password=... \
  -e Transcriber__Model=Small -e Transcriber__Language=pl kickgateway-transcriber
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
