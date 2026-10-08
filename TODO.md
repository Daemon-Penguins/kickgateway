# TODO

Open items from the chat-analytics work (REST `/api/analytics/*` + `TailoredApps.KickGateway.Mcp`).
Details: `docs/CHAT-ANALYTICS.md`.

## Ship it

- [ ] Commit the work on a feature branch and open a PR (right now it's uncommitted on `main`).
- [x] Add the `ANALYTICS_API_KEY` repo secret: a random value, at least 24 chars. `deploy.yml` already
      passes it to the Api as `Analytics__ApiKey`. If it's empty, only the admin cookie works.
- [ ] Dev: `dotnet user-secrets set Analytics:ApiKey "<key>" --project src/TailoredApps.KickGateway.Api`.
- [ ] Watch the first deploy:
  - [ ] The `AddChatAnalytics` migration applies on Api start. It's additive only (4 new tables).
  - [ ] The projector then backfills the **whole** inbox history, 500 rows per batch. Watch DB load. If it's too heavy,
        pause it with `Analytics__Projection__Enabled=false`.
  - [ ] The backfill is done when `GET /api/analytics/status` shows `pendingInboxRows = 0` for both sources.
- [ ] Publish the MCP server (`dotnet publish src/TailoredApps.KickGateway.Mcp -c Release -o …`) and register it
      in Claude Code / Claude Desktop. The commands are in `docs/CHAT-ANALYTICS.md`.

## Bug: `KickWebhookDispatcher` reads the wrong fields (found, not fixed)

Compared with Kick's documented payloads (docs.kick.com/events/event-types), these published contracts go out
with empty fields:

- [ ] `channel.subscription.gifts`: reads `recipients` → should be `giftees[]`. There is no `gift_count`;
      the count is the length of `giftees`.
- [ ] `kicks.gifted`: reads `gifter` / `amount` / `message` → should be `sender` / `gift.amount` / `gift.message`.
- [ ] `channel.reward.redemption.updated`: reads `user` / `redemption_id` → should be `redeemer` / `id`.
- [ ] Add unit tests with the documented payloads.
- [ ] Mention the fix in `docs/CLIENT-INTEGRATION.md` if subscribers relied on these fields.

The analytics projection (`ChatProjectionMapper`) already accepts both shapes, so analytics isn't affected.

## Check assumptions against production data

No production `RawBody` / `RawData` was available locally. The mappers were built from Kick's docs and the
existing `RealtimeFrameMapper`.

- [ ] Confirm that webhook `message_id` and Pusher `id` are the same UUID for the same chat message. Webhook +
      realtime dedupe relies on this; the fallback guard only catches the same sender + content within ±5 s.
      To check, look for near-duplicates in a channel covered by both sources:
      same `ChannelSlug` + `SenderUserId` + `Content`, `CreatedAt` within a few seconds, different `Source`.
- [ ] Check the realtime shapes against real `ReceivedRealtimeEvents.RawData`: `SubscriptionEvent`,
      `GiftedSubscriptionsEvent`, `KicksGifted`, `RewardRedeemedEvent`, `FollowersUpdated` (`followed` +
      `username`?), `MessageDeletedEvent`.
- [ ] After the backfill, spot-check a few `ChatMessages` / `ChatterEvents` rows against their source
      `RawBody` / `RawData`.
- [ ] Look at how many realtime `ChatterEvents` rows are left with an empty `UserId` (username not
      resolved to an id).

## Operations

- [ ] The projector assumes a single Api replica. Before scaling the Api out, add a lock (e.g.
      `sp_getapplock`) or move the projector into its own service.
- [ ] Rebuild is manual SQL for now (clear the tables + `AnalyticsCheckpoints`). Decide whether that's enough.
- [ ] Retention / GDPR: profiles are personal data. Neither the inboxes nor the read model are pruned yet.
  - [ ] Decide on a retention period.
  - [ ] Write down the erasure procedure: delete the person's rows from the read model **and** the inbox.
