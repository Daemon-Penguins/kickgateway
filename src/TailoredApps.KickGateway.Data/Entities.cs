using System.ComponentModel.DataAnnotations;

namespace TailoredApps.KickGateway.Api.Data;

/// <summary>
/// One row per Kick developer application. The gateway holds N of these so a
/// single deployment can host multiple OAuth apps (each with its own ClientId,
/// secret, configured RedirectUri / WebhookUrl on the Kick portal).
/// </summary>
public class KickClientApp
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(120)]
    public string Name { get; set; } = "";

    [MaxLength(200)]
    public string ClientId { get; set; } = "";

    [MaxLength(400)]
    public string ClientSecret { get; set; } = "";

    [MaxLength(500)]
    public string RedirectUri { get; set; } = "";

    [MaxLength(500)]
    public string Scopes { get; set; } = "events:subscribe channel:read channel:write user:read chat:write streamkey:read moderation:ban";

    /// <summary>Public webhook URL configured for this app in the Kick dev portal — surfaced for diagnostics.</summary>
    [MaxLength(500)]
    public string WebhookUrl { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When true, this client app is the one used for the admin SSO flow at
    /// <c>/api/auth/admin/login</c>. Exactly one row should carry this flag.
    /// Enforced by a filtered unique index in <see cref="KickGatewayDbContext"/>.
    /// </summary>
    public bool IsAdminLoginClient { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<KickBroadcasterAccount> Accounts { get; set; } = new();
}

/// <summary>How the OBS clip player orders a channel's clip pool.</summary>
public enum ClipsSortMode
{
    /// <summary>Newest first (Kick <c>sort=date</c>).</summary>
    Latest = 0,
    /// <summary>Most viewed first (Kick <c>sort=view</c>), within <see cref="ClipsTimeWindow"/>.</summary>
    MostViewed = 1,
}

/// <summary>Time window for <see cref="ClipsSortMode.MostViewed"/> (Kick <c>time=</c> param).</summary>
public enum ClipsTimeWindow
{
    All = 0,
    Day = 1,
    Week = 2,
    Month = 3,
}

/// <summary>
/// One broadcaster authenticated under a specific client app. A single Kick
/// channel can appear multiple times if connected under different clients.
/// </summary>
public class KickBroadcasterAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid KickClientAppId { get; set; }
    public KickClientApp? KickClientApp { get; set; }

    /// <summary>Numeric Kick user id (stored as string — Kick returns it numerically but our DTO carries strings).</summary>
    [MaxLength(64)]
    public string KickUserId { get; set; } = "";

    [MaxLength(120)]
    public string Username { get; set; } = "";

    [MaxLength(120)]
    public string ChannelSlug { get; set; } = "";

    [MaxLength(4000)]
    public string AccessToken { get; set; } = "";

    [MaxLength(4000)]
    public string RefreshToken { get; set; } = "";

    [MaxLength(500)]
    public string Scopes { get; set; } = "";

    /// <summary>UTC moment when the current access token expires. We refresh at AccessTokenExpiresAt - 5min.</summary>
    public DateTime AccessTokenExpiresAt { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When true, this channel's clips are served on the public OBS player page at
    /// <c>/obs/clips/{slug}</c>. Independent of <see cref="IsEnabled"/> (which gates
    /// event ingestion) so a channel can be opted out of the public page without
    /// disabling its webhooks.
    /// </summary>
    public bool ClipsDisplayEnabled { get; set; } = true;

    /// <summary>
    /// When true, the real-time listener captures this channel's live video (HLS) while it
    /// streams and forwards the raw segments through the broker (best-effort). Independent of
    /// <see cref="IsEnabled"/>. Only takes effect when the listener's global
    /// <c>Kick:Realtime:Video:Enabled</c> switch is on.
    /// </summary>
    public bool VideoCaptureEnabled { get; set; } = true;

    // === OBS clip player settings (per channel) ===

    /// <summary>Base ordering of the clip pool: newest first, or most viewed.</summary>
    public ClipsSortMode ClipsSortMode { get; set; } = ClipsSortMode.Latest;

    /// <summary>Time window for "most viewed" (ignored for Latest).</summary>
    public ClipsTimeWindow ClipsTimeWindow { get; set; } = ClipsTimeWindow.All;

    /// <summary>How many clips from the top of the sorted pool to play first, in order, before shuffling. Only applies when <see cref="ClipsShuffle"/> is true.</summary>
    public int ClipsLeadInCount { get; set; } = 2;

    /// <summary>True = play the lead-in clips, then random picks forever. False = play the whole pool in sorted order and loop.</summary>
    public bool ClipsShuffle { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the gateway successfully called /oauth/token with this refresh_token.</summary>
    public DateTime? LastRefreshedAt { get; set; }

    public List<KickEventSubscription> Subscriptions { get; set; } = new();
}

/// <summary>
/// A Kick channel the real-time (Pusher) listener follows, managed independently of the
/// webhook broadcaster roster. Realtime needs only a public <see cref="Slug"/> — no OAuth,
/// no client app, no subscription enrollment: the listener resolves the slug's
/// channel_id + chatroom_id via the sidecar and subscribes to the Pusher channels selected
/// by the per-event capture flags.
///
/// The listener's effective roster is the UNION of these rows (enabled) and the enabled
/// webhook broadcasters. When a slug appears in both, a given capture is followed if EITHER
/// source enables it (webhook broadcasters implicitly capture both chat + channel).
/// </summary>
public class RealtimeChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Kick channel slug (lowercase). The only required input; unique across the table.</summary>
    [MaxLength(120)]
    public string Slug { get; set; } = "";

    /// <summary>Optional friendly label for the admin UI (defaults to the slug when unset).</summary>
    [MaxLength(120)]
    public string? DisplayName { get; set; }

    /// <summary>Master gate — when false the listener ignores this channel entirely.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Subscribe to the chat channel <c>chatrooms.{chatroomId}.v2</c> — chat messages, deletions, bans/timeouts, pins, polls, chatroom mode changes.</summary>
    public bool CaptureChat { get; set; } = true;

    /// <summary>Subscribe to the broadcast channel <c>channel.{channelId}</c> — subs, gifted subs, follows, live/offline, host/raid, rewards, kicks.</summary>
    public bool CaptureChannel { get; set; } = true;

    /// <summary>
    /// When true, capture this channel's live video (HLS) while it streams and forward the raw
    /// segments through the broker (best-effort). Gated by the listener's global
    /// <c>Kick:Realtime:Video:Enabled</c> switch. Off by default — video is the heavy add-on and
    /// these channels are opt-in.
    /// </summary>
    public bool VideoCaptureEnabled { get; set; }

    /// <summary>Resolved Kick numeric channel id — cached from the last verify/resolve. Informational; the listener resolves live too.</summary>
    [MaxLength(64)]
    public string? ChannelId { get; set; }

    /// <summary>Resolved Kick chatroom id — cached from the last verify/resolve. Informational.</summary>
    [MaxLength(64)]
    public string? ChatroomId { get; set; }

    /// <summary>Last time the slug was resolved against Kick (via the admin "Verify" action). Null = never verified.</summary>
    public DateTime? LastResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Tracks which Kick event subscriptions the gateway has registered for a
/// broadcaster. Mirrors what's stored upstream — used to avoid double-subscribing.
/// </summary>
public class KickEventSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid KickBroadcasterAccountId { get; set; }
    public KickBroadcasterAccount? Broadcaster { get; set; }

    /// <summary>The id returned by Kick's /events/subscriptions POST — used for DELETE.</summary>
    [MaxLength(120)]
    public string KickSubscriptionId { get; set; } = "";

    [MaxLength(120)]
    public string EventType { get; set; } = "";

    public int Version { get; set; }

    [MaxLength(40)]
    public string Method { get; set; } = "webhook";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// PKCE challenge persisted across the OAuth roundtrip. Looked up by State on
/// callback, then deleted. TTL enforced by ExpiresAt — a background prune
/// could clean up old rows but isn't strictly needed for correctness.
/// </summary>
public class PkceStateEntry
{
    [MaxLength(128)]
    public string State { get; set; } = "";

    [MaxLength(256)]
    public string CodeVerifier { get; set; } = "";

    [MaxLength(256)]
    public string CodeChallenge { get; set; } = "";

    public Guid KickClientAppId { get; set; }

    [MaxLength(40)]
    public string Flow { get; set; } = "broadcaster";

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Idempotency record per Kick webhook delivery. Insert with PK = Kick-Event-Message-Id;
/// a duplicate insert fails fast and tells the receiver to ack the duplicate
/// without re-publishing. This is the "inbox" half of the inbox/outbox flow
/// (MassTransit's own Inbox protects consumers; this protects the publish step).
/// </summary>
public class ReceivedWebhook
{
    /// <summary>Primary key = Kick-Event-Message-Id (string from header).</summary>
    [MaxLength(120)]
    public string MessageId { get; set; } = "";

    [MaxLength(120)]
    public string EventType { get; set; } = "";

    [MaxLength(120)]
    public string SubscriptionId { get; set; } = "";

    public Guid? BroadcasterAccountId { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PublishedAt { get; set; }

    /// <summary>Raw request body as Kick sent it. Verbatim string used for signature verification — kept here so we can re-inspect what arrived even after the outbox row has shipped to RabbitMQ and been deleted.</summary>
    public string RawBody { get; set; } = "";

    /// <summary>HTTP headers Kick attached, joined as `Name: Value\n…`. Skipped for the unknown-broadcaster drop path (set on signature-verified deliveries only).</summary>
    public string? Headers { get; set; }
}

/// <summary>
/// Inbox/idempotency record per real-time (Kick Pusher) frame the listener ingests. Same
/// role as <see cref="ReceivedWebhook"/> for the webhook path: insert keyed on a stable
/// per-frame <see cref="DedupeKey"/> and publish through the EF outbox in the same
/// transaction, so the record + the RabbitMQ publish commit together. A duplicate insert
/// (Pusher redelivers recent messages on reconnect) fails fast and is skipped without
/// re-publishing.
/// </summary>
public class ReceivedRealtimeEvent
{
    /// <summary>Primary key — stable per-frame key (natural id where present, else a content hash).</summary>
    [MaxLength(200)]
    public string DedupeKey { get; set; } = "";

    /// <summary>Raw Pusher event name (e.g. <c>App\Events\ChatMessageEvent</c>).</summary>
    [MaxLength(160)]
    public string EventName { get; set; } = "";

    /// <summary>The Pusher channel the frame arrived on (e.g. <c>chatrooms.123.v2</c>).</summary>
    [MaxLength(120)]
    public string PusherChannel { get; set; } = "";

    /// <summary>Broadcaster slug (lowercase).</summary>
    [MaxLength(120)]
    public string Slug { get; set; } = "";

    [MaxLength(64)]
    public string? ChannelId { get; set; }

    [MaxLength(64)]
    public string? ChatroomId { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PublishedAt { get; set; }

    /// <summary>Raw inner JSON of the Pusher frame's <c>data</c> field.</summary>
    public string RawData { get; set; } = "";
}
