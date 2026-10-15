# Chat analytics (REST + MCP)

Read-only analysis of chat captured by the gateway. It covers who talks to whom
(interaction graph, cliques, bridges), what each chatter is like (a personal
profile), and what happens between two specific people. Two surfaces share one
backend:

- **REST**: `/api/analytics/*` on the Api. Accepts the admin cookie or an API key.
- **MCP server**: `src/TailoredApps.KickGateway.Mcp`. A thin adapter that
  exposes each endpoint as an MCP tool, so Claude Code, Claude Desktop or any
  other MCP client can explore the data conversationally. It is deployed as the
  `mcp` container at `https://<host>/mcp` (Streamable HTTP) and can also run
  locally over stdio.

## Data flow

```
ReceivedWebhooks.RawBody ──┐                        ┌─ ChatMessages   (one row per Kick chat message)
                           ├─ ChatProjectionService ┼─ ChatMentions   (@username per message)
ReceivedRealtimeEvents ────┘  (Api, background)     └─ ChatterEvents  (follow/sub/gift/kicks/reward/ban/timeout/unban/deletion)
   .RawData                                             AnalyticsCheckpoints (keyset position per inbox)
```

- The **projector** tails both inboxes in the Api process: batches of 500,
  a 30 s lag so in-flight webhook transactions aren't skipped, and keyset
  checkpoints. On first start it backfills the entire inbox history. Rows are
  keyed deterministically, so replays never duplicate anything.
- **Dedupe across sources.** Kick uses the same chat message UUID on webhooks
  and on Pusher, so the primary key merges the two copies. As a safety net, a
  realtime message with a different id but the same sender, content and
  timestamp (±5 s) as an existing webhook copy is dropped, and vice versa.
- **Webhooks are authoritative.** For channels that have an enabled webhook
  broadcaster, subs/gifts/kicks/rewards/follows/bans come from webhooks only.
  The Pusher stream contributes just the realtime-only kinds: message deletions
  and unbans. Realtime-only channels (the `/admin/realtime` roster) get
  everything from Pusher.
- Pusher frames often carry only a username. Those rows are resolved to a user
  id from the chat already seen; if a name can't be resolved, the row keeps the
  username and an empty id, and profile queries match it by username.
- All Kick JSON knowledge lives in `ChatProjectionMapper` (webhooks) and
  `RealtimeChatProjectionMapper` (Pusher).
- Subscription and gifted-sub events are stamped with the inbox receive time.
  The payload's `created_at` is the subscription's start date, repeated on
  every monthly renewal.

**Rebuild** (e.g. after a mapper fix): stop the Api, then
`DELETE FROM ChatMentions; DELETE FROM ChatMessages; DELETE FROM ChatterEvents; DELETE FROM AnalyticsCheckpoints;`,
then start it again. The projector replays both inboxes.

## Configuration (`Analytics` section on the Api)

| Key | Default | What |
| --- | --- | --- |
| `Analytics:ApiKey` | *(empty)* | Shared secret for machine clients. Empty or shorter than 24 chars = key access disabled (cookie only). |
| `Analytics:DefaultWindowDays` | `7` | Look-back for channel-level queries when `from` is omitted. |
| `Analytics:MaxGraphMessages` | `200000` | Messages loaded to build a graph (latest win; response says `truncated`). |
| `Analytics:MaxProfileMessages` | `20000` | Messages sampled for a profile's style/time stats (counts stay exact). |
| `Analytics:Projection:Enabled` | `true` | Run the projector in this process. |
| `Analytics:Projection:IncludeRealtime` | `true` | Also project `ReceivedRealtimeEvents`. |
| `Analytics:Projection:BatchSize` / `PollSeconds` / `LagSeconds` | `500` / `5` / `30` | Projector tuning. |

Prod: set the `ANALYTICS_API_KEY` repo secret (see README → Deploy). Dev: run
`dotnet user-secrets set Analytics:ApiKey <random ≥24 chars> --project src/TailoredApps.KickGateway.Api`.

## Reliability

- Every `/api/analytics/*` request runs its queries in a `READ UNCOMMITTED`
  transaction (`AnalyticsRequestFilter`). The data is a derived, read-only
  aggregate, so dirty reads are harmless. Shared locks were not harmless:
  during the initial backfill, analytics scans deadlocked with the
  projector's inserts and were killed as victims (HTTP 500 after about 5 s).
  Without shared locks, analytics reads can't block or deadlock the projector
  or the webhook/realtime ingest.
- Errors come back as JSON. A transient database error (deadlock, timeout,
  lock timeout) returns `503` with `Retry-After`. Anything else returns `500`
  with a `traceId`, which you can find in the Api log. The MCP server retries
  once on any 5xx.
- `status` counts pending inbox rows up to 100,000. A higher number means
  "at least that many".

## Auth & scope

- **Admin cookie**: SuperAdmin sees every channel. Other admins see the
  channels of broadcasters under client apps where they hold any role.
- **API key** (`Authorization: Bearer <key>` or `X-Api-Key: <key>`): reads
  analytics for every channel. It is accepted **only** by `/api/analytics/*`;
  every other admin endpoint ignores it. A request carrying a key is judged by
  the key alone, so a wrong key returns 401 instead of a login redirect.
- A channel or chatter outside the caller's scope returns 404.
- Non-SuperAdmin scopes are built from Kick broadcasters, so **Twitch** channels
  are visible to SuperAdmins and the API key only.

## Platforms

The read model holds Kick and Twitch rows side by side; every row carries
`platform` (`kick` or `twitch`). Channel slugs/logins and numeric user ids are
only unique **within** a platform, so every endpoint that names a channel or a
chatter takes `platform` and defaults it to `kick` (backward compatible).
`/status` and `/channels` cover both platforms unless `platform` narrows them;
each channel row says which it is. Twitch rows map onto the same shapes: a
cheer (bits) is a `kicks_gift` event (`amount` = bits), `clear_user_messages`
(Twitch's chat-side view of a ban or timeout) is a `timeout`, sub notices are
`subscription_new` / `subscription_renewal` / `subscription_gift` with the tier
in `detail`.

## Endpoints

All `GET`, JSON camelCase, nulls omitted, all times UTC (`…Z`). `from`/`to`
accept ISO-8601 (`2026-09-01`, `2026-09-01T18:00:00Z`, no offset = UTC),
look-backs (`30m`, `12h`, `7d`, `4w`), or `from=all`. A response `window`
without `from` means "since the beginning". `platform=kick|twitch` applies to
every endpoint below (see Platforms).

| Endpoint | Default window | Returns |
| --- | --- | --- |
| `/api/analytics/status` | – | Projection lag per source (`webhooks`, `realtime`, `twitch`), row counts, oldest/newest message, channels in scope, `transcripts` = speech-to-text coverage per channel (below) |
| `/api/analytics/channels` | 30 d | Channels with chat: platform, messages, chatters, first/last |
| `/api/analytics/channels/{slug}/overview` | 7 d | Messages, chatters, new vs returning, reply/mention share, per-hour + per-day activity, top 15 chatters, event totals, `transcripts` (speech in the window, when any) |
| `/api/analytics/channels/{slug}/chatters?sort=&limit=&offset=` | 7 d | Paged chatters; `sort` = `messages` / `active_days` / `replies` / `last_seen` / `first_seen` |
| `/api/analytics/channels/{slug}/interactions?maxNodes=&maxEdges=&proximity=&replyWeight=&mentionWeight=&giftWeight=&proximityWeight=` | 7 d | Interaction graph (below) |
| `/api/analytics/chatters?q=&channel=&limit=` | all | Find chatters by username fragment or id |
| `/api/analytics/chatters/{user}?channel=` | all | Personal profile (below). `{user}` = id or current/former username |
| `/api/analytics/chatters/{user}/relationships/{other}?channel=` | all | Pair dynamics (below) |
| `/api/analytics/messages?channel=&user=&q=&cursor=&limit=` | 7 d (all with `user`) | Messages newest first, `q` = substring, deleted ones flagged; page with `nextCursor` |
| `/api/analytics/messages/{messageId}/context?before=&after=` | – | Reply chain it answers, replies it got, surrounding channel messages |

### Interaction graph

A directed edge A→B means A addressed B. It is built from four signals:

| Signal | Source | Default weight |
| --- | --- | --- |
| `replies` | Kick reply feature (`replies_to` / `metadata.original_sender`) | 3 |
| `mentions` | `@username` in the text (a reply that also @s its target counts once) | 2 |
| `gifts` | gifted subs, gifter → giftee | 2 |
| `proximity` | Spoke within 20 s after B, **only in slow chat**: at most 3 other people spoke in that window, and the unit is split between them. A message with an explicit target never adds proximity. | 0.5 |

Per node: `inWeight` (attention received), `outWeight` (attention given),
replies/mentions sent and received, `partners` / `mutualPartners`,
`community` (Louvain cluster, 1 = largest), and `participation` (0..1, how
evenly the node's ties spread across communities; high = bridge).
Communities also report `cohesion`, the share of their tie weight that stays
inside. The summary lists `topHubs` (draw attention), `topInitiators`
(address others), `topConnectors` (bridges), plus `density`, `reciprocity`
and `modularity`. Nodes keyed `@name` were mentioned but never seen chatting.

### Chatter profile

- Identity and known usernames.
- Activity: first/last seen, active days, sessions (split on 30 min gaps),
  UTC hour and weekday histograms (weekday index 0 = Monday), peak hours.
- Per-channel presence, with that channel's badges, roles and sub months.
- Writing style: length, emote/emote-only/command/question/link/caps shares,
  top terms, top emotes.
- Social circle: the top 20 partners, with replies, mentions and gifts in each
  direction.
- Support: follows, subs, gifts given and received, kicks, reward redemptions.
- Moderation: bans and timeouts received, messages deleted, actions issued as
  a moderator.
- The 25 latest messages.

The stats are deliberately shallow. Tone, topics and intent are left to
whoever reads the messages, which is the LLM when you go through MCP.

### Relationship

For each direction: replies, mentions, gifted subs, weight, median reply
latency and last interaction. Also returned: a `balance` label (`mutual` or
`mostly X → Y`), shared channels, shared active days, moderation actions
between the two, and the latest 40 direct exchanges, each with the message it
replied to.

### Live transcripts (speech-to-text)

When `Subscribers.Transcriber` is running, the Api stores every `LiveTranscript` it publishes in
the `LiveTranscripts` table (consumer `LiveTranscriptConsumer`, shared durable queue
`kickgateway-live-transcripts`, one row per ~10-30 s slice, redeliveries deduplicated by
`DedupeKey`). This is a separate best-effort pipeline, not derived from the inboxes: a lost
message is a gap, and there is nothing to rebuild from.

| Endpoint | Returns |
|---|---|
| `GET /channels/{slug}/transcripts?from&to&q&language&cursor&limit&segments` | slices overlapping the window, oldest first; `q` = substring match on the text; `language` = ISO-639-1 filter (e.g. `de`); keyset `cursor` (max `limit` 500); `segments=false` = text only (no per-segment timings) |
| `GET /channels/{slug}/transcripts/at?at=<ISO-8601>&tolerance=20&segments` | what was being said at that moment (+/- tolerance seconds), e.g. pass a chat message's `createdAt` |
| `GET /channels/{slug}/transcripts/{id}/reactions?lagMin=5&lagMax=45&limit=100` | what chat wrote right after one slice: the slice with its `previous`/`next`, the messages in `[endedAt+lagMin, endedAt+lagMax)` oldest first (each with `secondsAfterEnd`), and `stats` — messages, chatters, per minute vs the 60 s before the slice started (`baseline…`, `ratio`), `firstReply`. `stats` count the whole window even when `limit` truncates the list |
| `GET /channels/{slug}/timeline?from&to&at&before&after&include&minConfidence&cursor&limit` | speech + events + chat of the channel in one chronology (below) |

Coverage, so a model knows whether speech exists before it concludes "the streamer never said
that": `/status` carries `transcripts` with, per channel in scope, the slice count, first/last
slice, when the newest one was stored (`lastReceiveLagSeconds` = pipeline delay) and the last 24 h
in detail (slices, `speechSeconds`, `averageConfidence`, slices per `language`); `/overview` carries
the same `transcripts` block for its window (slices that start in it), omitted when there are none.

Each item carries `startedAt`/`endedAt` (estimated UTC stream time, +/- one HLS segment),
`audioStartSeconds` (the transcriber's per-channel audio clock, for ordering/gap detection), the
`text`, `language` (the language the slice was transcribed in), `detectedLanguage` and
`languageProbability` (what Whisper's detector heard on the slice and how sure it was; `null` when the
transcriber ran with a fixed language), `translations` / `translatedSegments` (target language → text /
timed segments, when the translator produced one), per-segment `segments[]` with their own times, `confidence`,
`firstMediaSequence`/`lastMediaSequence` (HLS provenance), `model`, `transcribedAt` and `processingSeconds`.

Chronology with chat: everything is UTC on the gateway's clock, so `startedAt`/`endedAt` sort
together with `ChatMessages.CreatedAt` and the inboxes' `ReceivedAt`. Expect chat *reactions* to
a sentence ~10-20 s after its `endedAt` (encoder/HLS latency plus the viewers' player delay).
Do not order speech by the row's `receivedAt`: that is when the Api stored the slice, i.e. its
end plus inference plus transit, 10-30 s after the words were said.

### Channel timeline (speech + events + chat)

`GET /channels/{slug}/timeline` answers "what did the streamer say and how did chat respond"
without client-side joins: one list, oldest first, of

| `kind` | Source | `at` | Extra fields |
| --- | --- | --- | --- |
| `speech` | `LiveTranscripts` | `startedAt` of the slice | `endedAt`, `language`, `confidence`, `translations` (target language → text) |
| `event` | `ChatterEvents` | `occurredAt` | `eventKind` (`follow`, `subscription_new`, `subscription_renewal`, `subscription_gift`, `kicks_gift`, `reward_redemption`, `ban`, `timeout`, `message_deleted`, `unban`), `userId`, `counterpart`, `amount` |
| `chat` | `ChatMessages` | `createdAt` | `userId`, `replyToMessageId`, `replyToUsername` |

Common fields: `id` (transcript row id / event id / message id), `who` (the channel slug for
speech - it is the stream's audio, streamer plus surroundings - and the username otherwise) and
`text` (transcript, message content or event detail). At equal timestamps the order is speech,
event, chat, so a slice is read before the chat written while it was being said.

Parameters: `from`/`to` as elsewhere (default: the hour before `to`; `from=all` for an unbounded
start), **or** `at` (ISO-8601 or a look-back like `15m`) with `before`/`after` seconds (default
60/180) to read around one moment; `include` = comma-separated subset of `speech,chat,events`;
`minConfidence` drops speech below it; `limit` 1-500 (default 200); `cursor` from `nextCursor`.
Speech slices that start up to 120 s before the window but end inside it are included. The
response's `chronology` string restates the timing conventions for machine readers.

Cost: one page is at most three bounded index seeks - `(ChannelSlug, StartedAt)`,
`(ChannelSlug, OccurredAt)`, `(ChannelSlug, CreatedAt)`, each `TOP limit+1` - plus one range read
of the translations for the speech rows on the page, merged in memory. The keyset cursor is
`(at, kind rank, id)`, so paging through many rows that share a second (chat has second precision)
is exact. Like every analytics query it runs READ UNCOMMITTED and never touches the ingest paths.

## MCP server

`TailoredApps.KickGateway.Mcp` is an MCP server built on the official C# SDK
(`ModelContextProtocol`). All of its tools are read-only:

`analytics_status`, `list_channels`, `channel_overview`, `list_chatters`,
`interaction_graph`, `find_chatters`, `chatter_profile`,
`chatter_relationship`, `search_messages`, `message_context`, and for channels
with live transcripts `search_transcripts`, `transcript_at`, `speech_reactions`
(the chat that followed one sentence, with its rate against the minute before)
and `stream_timeline` (speech + events + chat in one chronology). The transcript
tools default to `segments=false` (text only) to keep responses small.

### Remote: the deployed `mcp` container (no local install)

`deploy.yml` runs it as the `mcp` service. Traefik routes `https://<host>/mcp`
(plus the OAuth well-known paths) to it, it calls the Api over the internal
network (`http://api:8080`), and clients authenticate in one of two ways. Both
end at the same tools, and the server fails closed: a missing or short
`ANALYTICS_API_KEY` rejects every request.

**API key** — the analytics key as `Authorization: Bearer` or `X-Api-Key`, for
clients that let you set a header:

```bash
claude mcp add --transport http kick-chat-analytics https://<host>/mcp \
  --header "Authorization: Bearer <key>"
```

Claude Code stores the header in its config. Use a client-specific secret
store if you'd rather not keep the key in plain text.

**OAuth 2.1** — for clients that only speak OAuth (ChatGPT connectors and the
Apps SDK, Claude.ai custom connectors, the MCP Inspector…). The container
embeds a small authorization server, so nothing external is needed:

- *Discovery*: `/.well-known/oauth-protected-resource` (RFC 9728, also at
  `/.well-known/oauth-protected-resource/mcp`) and
  `/.well-known/oauth-authorization-server` (RFC 8414, also as
  `/.well-known/openid-configuration`). An unauthenticated `/mcp` request gets
  `401` with `WWW-Authenticate: Bearer resource_metadata="…", scope="analytics"`.
- *Clients*: Client ID Metadata Documents (an `https://…/client.json` URL as
  the client id — what ChatGPT uses; such a client authenticates at the token
  endpoint either as a public client or with `private_key_jwt`, a signed
  assertion checked against its `jwks_uri` — ChatGPT sends the latter, without
  a `client_id` in the body) and RFC 7591 dynamic registration at
  `/mcp/oauth/register` (Claude.ai, Claude Code, the Inspector). Registration
  is open; the registered redirect URIs are what a client is later held to.
- *Flow*: authorization code + PKCE (`S256` required) at
  `/mcp/oauth/authorize`, tokens at `/mcp/oauth/token`, the RFC 8707
  `resource` checked against `https://<host>/mcp`, `iss` echoed in the redirect
  (RFC 9207). The consent page asks for the analytics API key — that is the
  "login"; it shows the client's name and redirect host. Access tokens live
  60 min, refresh tokens are rotated, and the grant as a whole expires after
  90 days (then the client re-prompts). One scope: `analytics`.
- *Stateless*: client ids, codes and tokens are HMAC-signed claims (the key is
  derived from the API key unless `Mcp__OAuth__SigningKey` is set), so restarts
  and replicas need no shared store, and rotating the API key revokes every
  token at once. Replay protection for codes and rotated refresh tokens is
  in-memory, per instance.

To connect ChatGPT: *Settings → Connectors → Create* (Developer mode), server
URL `https://<host>/mcp`, authentication **OAuth**, client id/secret left
empty. ChatGPT discovers the metadata and opens the consent page; paste the key
and approve. Claude.ai custom connectors work the same way. The Responses API
does not need OAuth: pass the key in the `mcp` tool's `headers`.

`Mcp__PublicBaseUrl` (deploy.yml sets `https://${PUBLIC_HOST}`) pins the
issuer and resource URLs; without it they are derived from the forwarded
request headers. `Mcp__OAuth__Enabled=false` turns OAuth off (API key only).

### Local: stdio

Configuration (environment variables, or user-secrets on the Mcp project):

| Key | What |
| --- | --- |
| `KickGateway__BaseUrl` | Gateway root URL, e.g. `https://gateway.example.com` (default `http://localhost:5286`) |
| `KickGateway__ApiKey` | Same value as the gateway's `Analytics__ApiKey` |
| `KickGateway__TimeoutSeconds` | HTTP timeout (default 90) |
| `Mcp__Transport` | `http` = Streamable HTTP at `/mcp` (what the container sets); default stdio. `--http` works too. |
| `Mcp__ClientApiKey` | HTTP mode only: key clients must present (and the OAuth consent page asks for). Defaults to `KickGateway__ApiKey`. |
| `Mcp__PublicBaseUrl` | HTTP mode only: public origin (`https://host`, no path) used for the OAuth issuer, resource and endpoint URLs. |
| `Mcp__OAuth__Enabled` | HTTP mode only: the embedded OAuth 2.1 server (default `true`). |
| `Mcp__OAuth__SigningKey` | Optional separate token-signing secret; default derives from the client key. |
| `Mcp__OAuth__AccessTokenMinutes` / `RefreshTokenDays` / `AuthorizationCodeSeconds` | Lifetimes (defaults 60 / 90 / 300). |
| `Mcp__OAuth__AllowClientMetadataDocuments` / `AllowDynamicRegistration` | Which client registration styles to accept (both default `true`). |

Build it once, since the MCP client launches the binary:

```bash
dotnet publish src/TailoredApps.KickGateway.Mcp -c Release -o ~/tools/kick-mcp
# optional: keep the key out of client config files
dotnet user-secrets set KickGateway:ApiKey "<key>" --project src/TailoredApps.KickGateway.Mcp
```

**Claude Code**

```bash
claude mcp add kick-chat-analytics \
  -e KickGateway__BaseUrl=https://gateway.example.com \
  -e KickGateway__ApiKey=<key> \
  -- ~/tools/kick-mcp/TailoredApps.KickGateway.Mcp
```

**Claude Desktop** (`claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "kick-chat-analytics": {
      "command": "C:\\tools\\kick-mcp\\TailoredApps.KickGateway.Mcp.exe",
      "env": {
        "KickGateway__BaseUrl": "https://gateway.example.com",
        "KickGateway__ApiKey": "<key>"
      }
    }
  }
}
```

Prompts that work well:

- "Who are the core regulars in `<slug>` this week, and which groups don't talk to each other?"
- "Build a profile of `<user>`."
- "How do `<a>` and `<b>` interact? Read their exchanges before concluding."

## Personal data

Profiles aggregate personal data about chatters (usernames, behaviour,
messages). Access is admin- and key-gated and scoped per client app. The read
model has no retention of its own: it mirrors the inboxes. For an erasure
request, delete the person's rows from `ChatMessages`/`ChatterEvents` **and**
the matching inbox rows. Otherwise a rebuild would bring them back.
