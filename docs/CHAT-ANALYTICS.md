# Chat analytics (REST + MCP)

Read-only analysis of chat captured by the gateway. It covers who talks to whom
(interaction graph, cliques, bridges), what each chatter is like (a personal
profile), and what happens between two specific people. Two surfaces share one
backend:

- **REST**: `/api/analytics/*` on the Api. Accepts the admin cookie or an API key.
- **MCP server**: `src/TailoredApps.KickGateway.Mcp`. A thin stdio adapter that
  exposes each endpoint as an MCP tool, so Claude Code, Claude Desktop or any
  other MCP client can explore the data conversationally.

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

## Auth & scope

- **Admin cookie**: SuperAdmin sees every channel. Other admins see the
  channels of broadcasters under client apps where they hold any role.
- **API key** (`Authorization: Bearer <key>` or `X-Api-Key: <key>`): reads
  analytics for every channel. It is accepted **only** by `/api/analytics/*`;
  every other admin endpoint ignores it. A request carrying a key is judged by
  the key alone, so a wrong key returns 401 instead of a login redirect.
- A channel or chatter outside the caller's scope returns 404.

## Endpoints

All `GET`, JSON camelCase, nulls omitted, all times UTC (`…Z`). `from`/`to`
accept ISO-8601 (`2026-09-01`, `2026-09-01T18:00:00Z`, no offset = UTC),
look-backs (`30m`, `12h`, `7d`, `4w`), or `from=all`. A response `window`
without `from` means "since the beginning".

| Endpoint | Default window | Returns |
| --- | --- | --- |
| `/api/analytics/status` | – | Projection lag per source, row counts, oldest/newest message, channels in scope |
| `/api/analytics/channels` | 30 d | Channels with chat: messages, chatters, first/last |
| `/api/analytics/channels/{slug}/overview` | 7 d | Messages, chatters, new vs returning, reply/mention share, per-hour + per-day activity, top 15 chatters, event totals |
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

## MCP server

`TailoredApps.KickGateway.Mcp` is a stdio MCP server built on the official C#
SDK (`ModelContextProtocol`). All of its tools are read-only:

`analytics_status`, `list_channels`, `channel_overview`, `list_chatters`,
`interaction_graph`, `find_chatters`, `chatter_profile`,
`chatter_relationship`, `search_messages`, `message_context`.

Configuration (environment variables, or user-secrets on the Mcp project):

| Key | What |
| --- | --- |
| `KickGateway__BaseUrl` | Gateway root URL, e.g. `https://gateway.example.com` (default `http://localhost:5286`) |
| `KickGateway__ApiKey` | Same value as the gateway's `Analytics__ApiKey` |
| `KickGateway__TimeoutSeconds` | HTTP timeout (default 90) |

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
