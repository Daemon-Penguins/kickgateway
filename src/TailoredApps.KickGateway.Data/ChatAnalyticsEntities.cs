using System.ComponentModel.DataAnnotations;

namespace TailoredApps.KickGateway.Api.Data;

// Chat-analytics read model. Rows here are DERIVED from the two inboxes — ReceivedWebhook.RawBody
// (signed Kick webhooks) and ReceivedRealtimeEvent.RawData (Pusher frames) — by the Api's
// ChatProjectionService; they are never written by the ingest hot paths. Everything can be
// rebuilt: clear these tables + the AnalyticsCheckpoint rows and the projector replays both
// inboxes from the beginning.

/// <summary>Which inbox a read-model row was projected from.</summary>
public enum ChatSource
{
    /// <summary><see cref="ReceivedWebhook"/> (Kick webhooks).</summary>
    Webhook = 1,
    /// <summary><see cref="ReceivedRealtimeEvent"/> (Kick Pusher stream).</summary>
    Realtime = 2,
}

/// <summary>
/// One chat message (<c>chat.message.sent</c>), normalised out of the webhook JSON so the
/// analytics queries can filter/aggregate by channel, sender, reply target and time with indexes
/// instead of parsing <see cref="ReceivedWebhook.RawBody"/> on every request.
/// </summary>
public class ChatMessageRecord
{
    /// <summary>Kick's chat message id (webhook <c>message_id</c> / Pusher <c>id</c>). Primary key —
    /// the same chat message seen twice (two client apps, or webhook + realtime) collapses to one row.</summary>
    [MaxLength(64)]
    public string MessageId { get; set; } = "";

    /// <summary>Inbox the row was first projected from.</summary>
    public ChatSource Source { get; set; }

    /// <summary>Inbox key of that delivery: <see cref="ReceivedWebhook.MessageId"/> or <see cref="ReceivedRealtimeEvent.DedupeKey"/>.</summary>
    [MaxLength(200)]
    public string SourceId { get; set; } = "";

    public Guid? BroadcasterAccountId { get; set; }

    [MaxLength(64)]
    public string BroadcasterUserId { get; set; } = "";

    /// <summary>Broadcaster channel slug (lowercase).</summary>
    [MaxLength(120)]
    public string ChannelSlug { get; set; } = "";

    [MaxLength(64)]
    public string SenderUserId { get; set; } = "";

    /// <summary>Username as it was when the message was sent (users can rename).</summary>
    [MaxLength(120)]
    public string SenderUsername { get; set; } = "";

    [MaxLength(120)]
    public string? SenderChannelSlug { get; set; }

    public bool SenderIsVerified { get; set; }

    [MaxLength(32)]
    public string? SenderColor { get; set; }

    /// <summary>Channel-specific badges at send time, compact <c>type:count|type:count</c>
    /// (e.g. <c>subscriber:14|moderator:0</c>). See <c>ChatBadges</c> in the Api for parsing.</summary>
    [MaxLength(1000)]
    public string? SenderBadges { get; set; }

    /// <summary>Message text as Kick sent it (emotes inline as <c>[emote:id:name]</c>). Truncated to 2000 chars.</summary>
    [MaxLength(2000)]
    public string Content { get; set; } = "";

    /// <summary>Kick's <c>created_at</c>, falling back to the webhook receive time.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Set when the message used Kick's reply feature (<c>replies_to</c>).</summary>
    [MaxLength(64)]
    public string? ReplyToMessageId { get; set; }

    [MaxLength(64)]
    public string? ReplyToUserId { get; set; }

    [MaxLength(120)]
    public string? ReplyToUsername { get; set; }

    public List<ChatMention> Mentions { get; set; } = new();
}

/// <summary>An <c>@username</c> mention inside a chat message (lowercase, deduped per message).
/// Stored by username because the mentioned user may never have chatted — ids are resolved at
/// query time.</summary>
public class ChatMention
{
    [MaxLength(64)]
    public string MessageId { get; set; } = "";
    public ChatMessageRecord? Message { get; set; }

    [MaxLength(120)]
    public string MentionedUsername { get; set; } = "";
}

/// <summary>What a <see cref="ChatterEvent"/> row records.</summary>
public enum ChatterEventKind
{
    /// <summary><c>channel.followed</c> — actor followed the channel.</summary>
    Follow = 1,
    /// <summary><c>channel.subscription.new</c> — <see cref="ChatterEvent.Amount"/> = duration (months).</summary>
    SubscriptionNew = 2,
    /// <summary><c>channel.subscription.renewal</c> — <see cref="ChatterEvent.Amount"/> = duration (months).</summary>
    SubscriptionRenewal = 3,
    /// <summary><c>channel.subscription.gifts</c> — one row per giftee: actor = gifter, counterpart = giftee.</summary>
    SubscriptionGift = 4,
    /// <summary><c>kicks.gifted</c> — <see cref="ChatterEvent.Amount"/> = kicks.</summary>
    KicksGift = 5,
    /// <summary><c>channel.reward.redemption.updated</c> — <see cref="ChatterEvent.Detail"/> = reward title, <see cref="ChatterEvent.RefId"/> = redemption id.</summary>
    RewardRedemption = 6,
    /// <summary><c>moderation.banned</c> without expiry — actor = banned user, counterpart = moderator.</summary>
    Ban = 7,
    /// <summary><c>moderation.banned</c> with an expiry (timeout) — actor = banned user, counterpart = moderator.</summary>
    Timeout = 8,
    /// <summary>Realtime <c>MessageDeletedEvent</c> — actor = author of the deleted message, <see cref="ChatterEvent.RefId"/> = message id,
    /// <see cref="ChatterEvent.Detail"/> = <c>ai</c> when Kick's AI moderation removed it.</summary>
    MessageDeleted = 9,
    /// <summary>Realtime <c>UserUnbannedEvent</c> — actor = unbanned user, counterpart = moderator.</summary>
    Unban = 10,
}

/// <summary>
/// A non-chat event attributed to a chatter (follow, sub, gift, kicks, reward, ban/timeout,
/// deleted message), projected from either inbox. "Actor" is <see cref="UserId"/>; the optional
/// counterpart is the other person involved (giftee, moderator). For channels that have a webhook
/// broadcaster, webhooks are authoritative and only realtime-only kinds (deletions, unbans) are
/// taken from the Pusher stream, so nothing is counted twice.
/// </summary>
public class ChatterEvent
{
    /// <summary>Deterministic key derived from the inbox key (+ suffix) so re-projection is idempotent.</summary>
    [MaxLength(260)]
    public string Id { get; set; } = "";

    public ChatSource Source { get; set; }

    [MaxLength(200)]
    public string SourceId { get; set; } = "";

    public ChatterEventKind Kind { get; set; }

    public Guid? BroadcasterAccountId { get; set; }

    [MaxLength(120)]
    public string ChannelSlug { get; set; } = "";

    /// <summary>Actor user id. Empty for anonymous actors (anonymous gifter) and for realtime events that
    /// carry only a username which couldn't be resolved to an id yet.</summary>
    [MaxLength(64)]
    public string UserId { get; set; } = "";

    [MaxLength(120)]
    public string Username { get; set; } = "";

    [MaxLength(64)]
    public string? CounterpartUserId { get; set; }

    [MaxLength(120)]
    public string? CounterpartUsername { get; set; }

    /// <summary>Kind-specific quantity: sub months, kicks, gifted subs (1 per giftee row).</summary>
    public int Amount { get; set; }

    /// <summary>Kind-specific text: reward title, ban reason, kicks message, gift tier.</summary>
    [MaxLength(500)]
    public string? Detail { get; set; }

    /// <summary>Kind-specific reference (reward redemption id — one redemption can be updated several times).</summary>
    [MaxLength(120)]
    public string? RefId { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>Timeout expiry / subscription expiry when Kick sends one.</summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Keyset position of a background projector over an append-only inbox table: the
/// (ReceivedAt, key) of the last row it processed. Delete the row to replay from the beginning.
/// </summary>
public class AnalyticsCheckpoint
{
    [MaxLength(80)]
    public string Name { get; set; } = "";

    public DateTime Position { get; set; }

    [MaxLength(120)]
    public string PositionKey { get; set; } = "";

    public long ProcessedCount { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
