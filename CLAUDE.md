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
  contracts live in `TailoredApps.KickGateway.Contracts.Channels`. A live snapshot also resolves the
  broadcast's `LiveVodId`/`LiveVodUrl` via `IKickVideosClient.GetLiveVodIdAsync` (videos listing ->
  `/api/v1/video/{uuid}` -> `livestream.vod_id`; cached per livestream id, 60 s negative cache, 10 s
  budget so stats never hang on it). Watch URLs are built ONLY from `vod_id` (`KickWatchUrls.Vod`,
  precomputed as `WatchUrl`/`LiveVodUrl`); the legacy `video.uuid` 404s - verified against kick.com's
  own links on 2026-10-09.
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
- **Live transcription is a subscriber, not part of the listener.**
  `TailoredApps.KickGateway.Subscribers.Transcriber` consumes `LiveVideoSegment` (throwaway
  non-durable queue, one segment at a time per process for ordering), keeps **one long-lived
  ffmpeg per channel** (`AudioDecoder`: segments → stdin, `-vn` → 16 kHz mono f32 PCM on
  stdout; fMP4 init written first, re-emitted inits deduped, pre-init media buffered + replayed, new init / ffmpeg exit → restart
  and re-sync the audio clock), cuts the PCM on the quietest 100 ms near `ChunkSeconds`
  (`AudioChunker`), maps chunk positions back to stream time from the HLS segment timeline
  (`SegmentTimeline`, ± one segment) and runs **one shared Whisper processor** (Whisper.net /
  whisper.cpp, no cross-call context so channels never bleed; GPU via Vulkan/CUDA when the
  runtime loads, CPU fallback; GGML model downloaded on first run into `Transcriber:ModelDir`).
  Output: `Contracts.Realtime.Media.LiveTranscript` published **directly via `IBus`** (this
  service has no DB → no outbox; no TTL) on its own slug-routed exchange
  (`KickMediaTopology.BindLiveTranscript`) + per-channel daily `.txt`/`.jsonl` files. Silence
  (RMS) is skipped before Whisper, hallucinations are dropped after (`TranscriptFilter`: blank,
  sound tags, suppress phrases, repetition, confidence floor). Back-pressure = **drop oldest**
  pending chunk (`MaxPendingChunks`) — stay live with gaps rather than fall behind. ffmpeg is a
  hard requirement (fail fast at startup). Container detection must **sniff the bytes**
  (`LiveVideoSegmentExtensions.DetectContainer`): Kick's playback CDN serves MPEG-TS as
  `application/octet-stream` with `.ts?dna=...` URLs, so MIME and `EndsWith(".ts")` both lie
  (that bug made the first prod run wait forever for an fMP4 init). Pure logic (chunker, timeline, filter) is unit-tested;
  the ffmpeg/Whisper path is not. A GPU fault surfaces as `SEHException` from whisper.cpp and can
  leave the next native call hung forever, so every Whisper call runs under a watchdog
  (`Transcriber:InferenceTimeoutSeconds`) with escalating recovery: rebuild processor -> reload
  model -> CPU backend -> stop the process (orchestrator restarts it). Never await a hung native
  dispose without a timeout. Root cause found on the dev box (Intel Arc Pro B50, driver
  32.0.101.8805): ggml-vulkan's KHR_coopmat shaders fault after 9-84 inferences; with them off
  (`Transcriber:DisableVulkanCoopmat`, default true) 100+ chunks ran clean at ~45 % lower speed.
  ggml reads that switch via the C runtime's `getenv`, so `NativeEnvironment.Set` pushes it through
  `_putenv_s` (ucrtbase) before the native lib loads - `Environment.SetEnvironmentVariable` alone is
  invisible to native code on Windows. Native whisper.cpp/ggml log lines are forwarded to ILogger
  (`LogProvider`); the device list (`ggml_vulkan: N = ...`) is the first thing to check on a GPU
  problem. Whisper.net creates a new whisper state per call (GPU buffers alloc/free each chunk) -
  measured: no VRAM growth. `Transcriber:Uncensored` bans `*`-containing tokens via whisper.cpp
  `suppress_regex` (Whisper learned subtitle-style `k***a` censoring); `Threads` auto = cores-2
  clamped 4..16 (whisper.cpp's own default of 4 made the CPU fallback 3x slower than needed).
  **Language is detected per chunk, sticky per channel.** `Transcriber:Languages` (default `pl,en,de`) is
  the candidate list: every chunk >= 3 s first goes through `DetectLanguageWithProbability` restricted to
  those codes (one extra encoder pass, ~0.5 s on a GPU), and `LanguageTracker` switches the channel only
  when the SAME other language is read confidently (`LanguageSwitchMinProbability`, 0.7) in
  `LanguageSwitchConfirmChunks` (2) consecutive chunks - measured live: background music and Whisper's
  silence fillers ("I'm sorry.") score 0.6-0.9 for the wrong language, so one confident chunk is not
  evidence; an unsure reading or the current language breaks the streak. Each language gets its own processor from
  the same loaded model (the prompt, incl. the profanity list, is per language). `Transcriber:Language` is
  the start/fallback language; `auto` = Whisper's unrestricted pick (`WithLanguageDetection()` sets
  language `""`, which still transcribes - NOT the detect-only flag); empty `Languages` = fixed language.
  `LiveTranscript.Language` is what the slice was transcribed in, `DetectedLanguage`/`LanguageProbability`
  what the detector heard (null when detection did not run); both are stored (`AddTranscriptLanguageDetection`)
  and `/transcripts?language=de` filters on the former. Related filter rule: on music Whisper reads the
  profanity prompt back ("pojebane, pojebane, pojebane", "shit, bitch, shit, bitch"), so `TranscriptFilter`
  drops prompt echoes (same prompt word 3x, or >= 4 words at least half from `WhisperWorker.ProfanityVocabulary`).
- **The subtitles site is a text relay, never a video relay.** `TailoredApps.KickGateway.Subtitles`
  (`subtitles` container, own hostname via `SUBTITLES_HOST`, opt-in `SUBTITLES_ENABLED`) consumes
  `LiveTranscript` on a throwaway per-instance queue (`KickMediaTopology.BindLiveTranscript`, non-durable,
  auto-delete - a late caption is useless) into an in-memory per-slug backlog + fan-out (`TranscriptFeed`,
  bounded mailboxes, DropOldest) and streams it as SSE (`/{slug}/events`, ids per slug so `Last-Event-ID`
  resumes). The page embeds Kick's own player (`player.kick.com` iframe): Kick's IVS playback token
  carries `aws:access-control-allow-origin` = kick.com origins only (verified 2026-10-10 - a foreign
  Origin gets no ACAO header), so a self-hosted/delayed player would need the VPS to relay video.
  The public page doesn't; captions trail the picture by ~ChunkSeconds + inference - player buffer (use
  5 s chunks for ~2-4 s). The ONE exception is the token-gated **delayed player** (`/{slug}/player?token=`,
  `Subtitles:Relay`): the listener already pulls the live HLS for transcription, so `VideoRelay` keeps
  the last ~90 s of `LiveVideoSegment` per channel in memory and serves it as a live playlist
  (`HlsPlaylist`: our own contiguous numbering, `#EXT-X-DISCONTINUITY` on a hole or a Kick discontinuity,
  `#EXT-X-PROGRAM-DATE-TIME` = `CapturedAt - Duration` = the transcriber's `SegmentTimeline` clock) and
  the page's hls.js sits `delay` s behind the edge, timing captions on `hls.playingDate`. Video then
  leaves the VPS at the captured bitrate per viewer, hence: off without `Subtitles:Relay:Token`, viewer
  cap per channel (`cid` on playlist polls, 429 beyond), served-bytes log every 5 min, and the capture
  cap `Kick:Realtime:Video:MaxBitrateKbps` lowered to 1500 (~480p; audio is identical for Whisper).
  Assets live under `/_/` so they can't collide with a slug; slugs are validated
  (`[a-z0-9_-]{1,64}`) before reaching the player URL or a queue binding. No DB, no auth (public like
  `/obs/clips`; `Subtitles:Channels` is the allowlist - a CSV string, not an array, so one env var sets it).
- **Translation is a separate subscriber, one LLM call per slice, published as its own contract.**
  `TailoredApps.KickGateway.Subscribers.Translator` consumes `LiveTranscript` on a throwaway queue and, for
  slices whose `Language` is in `Translator:SourceLanguages` (default `de`) and not the target (`pl`),
  above `MinConfidence` (0.45) and younger than `MaxAgeSeconds` (120) (`TranslationPolicy`), sends the
  segments to the provider (`Translator:Provider:Name`): DeepL by default (text array in, one translation
  per line out, whole slice as `context`; free keys end with `:fx` and hit api-free), or an LLM via any
  OpenAI-compatible `/chat/completions` endpoint (Ollama needs no key) or Anthropic - LLMs get numbered
  lines (`TranslationPrompt`) and must echo the numbering; `stub` = no network, for wiring - and publishes
  `Contracts.Realtime.Media.LiveTranscriptTranslation` (translated segments keep the SOURCE timings; a
  broken numbering falls back to one segment for the slice) directly via the bus - no DB, no outbox.
  Correlation with the transcript is (slug, StartedAt, AudioStartSeconds) = the Api's `DedupeKey`
  inputs. The Api stores it in `LiveTranscriptTranslations` (unique per key + target language, no FK -
  the transcript may land later) and joins it on read (`translations`, `translatedSegments`); the
  subtitles site merges it into the backlog item and pushes a `translation` SSE event under the same id,
  and both pages default to the `auto` track (foreign-language line -> translation, Polish as spoken).
  The profanity prompt convention carries over: the system prompt forbids censoring.
  **Persistence lives in the Api**, not the transcriber: `LiveTranscriptConsumer` (shared durable
  queue `kickgateway-live-transcripts`, binds every slug) stores each slice in `LiveTranscripts`
  (`LiveTranscriptRecord`, migration `AddLiveTranscripts`), idempotent via `DedupeKey` =
  slug|StartedAt ticks|AudioStartSeconds (checked, plus a unique index for the replica race).
  Read via `/api/analytics/channels/{slug}/transcripts` (+ `/at`), same auth/scope/READ UNCOMMITTED
  as the rest of analytics (`TranscriptQueries`). Not a derived read model: nothing to rebuild from.
  **VPS deploy is opt-in and CPU-only**: `deploy.yml` ships the `transcriber` compose service behind
  the `transcriber` compose profile (`TRANSCRIBER_ENABLED=true` -> `COMPOSE_PROFILES` in `.env`;
  the deploy step also stops/removes the container when off, because `compose up` ignores
  disabled-profile containers that are still running). Measured on the VPS: `LargeV3Turbo` at
  ~0.5x real time -> most chunks dropped, so the intended production host is a GPU box (e.g. a
  Mac mini M2 via Metal - `deploy/macos/install-transcriber.sh` installs a launchd user agent;
  Whisper.net's base runtime is Metal-enabled on osx-arm64, no code changes). **Exactly one
  transcriber per channel**: `DedupeKey` is built from the per-process audio clock, so two
  producers store every sentence twice. Model via `TRANSCRIBER_MODEL` (default `LargeV3Turbo`),
  GGML cached in the `transcriber-models` volume, `cpu_shares: 512`, files off by default.
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
  parsing, HLS manifest rewrite, transcriber chunking/timeline/filter). Run with `dotnet test`.
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
│   ├── TailoredApps.KickGateway.Subscribers.VideoRecorder/ # Sample: reassembles LiveVideoSegment into playable files
│   ├── TailoredApps.KickGateway.Subscribers.Transcriber/   # Live speech-to-text: LiveVideoSegment → ffmpeg → Whisper → LiveTranscript (+ Dockerfile)
│   ├── TailoredApps.KickGateway.Subtitles/         # Live-subtitles site: /{slug} = Kick's player iframe + LiveTranscript captions over SSE (+ Dockerfile)
│   ├── TailoredApps.KickGateway.Subscribers.Translator/    # LiveTranscript (de) → LLM → LiveTranscriptTranslation (pl) (+ Dockerfile)
│   ├── TailoredApps.KickGateway.AppHost/           # Aspire orchestrator (F5 entrypoint)
│   └── TailoredApps.KickGateway.ServiceDefaults/   # OTel/health/resilience shared
├── docker/docker-compose.yml                       # fallback dev infra without Aspire
├── docker/clips-fetcher/                           # browser-TLS fetch proxy (clips past Cloudflare)
├── deploy/macos/                                   # launchd installer + env template for running the transcriber on a Mac (Metal)
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
