using System.ComponentModel.DataAnnotations;

namespace TailoredApps.KickGateway.Api.Data;

/// <summary>Platform discriminator values used across the analytics read model and the transcript tables.</summary>
public static class ChatPlatforms
{
    public const string Kick = "kick";
    public const string Twitch = "twitch";

    public static readonly string[] All = [Kick, Twitch];

    /// <summary>Lowercased, trimmed; null/empty = <see cref="Kick"/> (the value every pre-Twitch row carries).</summary>
    public static string Normalize(string? platform)
    {
        var p = (platform ?? "").Trim().ToLowerInvariant();
        return p.Length == 0 ? Kick : p;
    }

    public static bool IsKnown(string? platform) => Array.IndexOf(All, Normalize(platform)) >= 0;
}

/// <summary>
/// A Twitch channel the Twitch listener follows. Needs only the public <see cref="Login"/>: chat is read
/// with the gateway's own bot account (<see cref="TwitchBotAccount"/>), stream state is polled from Helix,
/// audio is pulled like the web player does. Resolved ids are cached here (Verify / listener roster refresh).
/// </summary>
public class TwitchChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Twitch login (lowercase, as in twitch.tv/&lt;login&gt;). Unique.</summary>
    [MaxLength(64)]
    public string Login { get; set; } = "";

    /// <summary>Numeric Twitch user id, resolved via Helix. Required for EventSub conditions; empty until verified.</summary>
    [MaxLength(64)]
    public string? UserId { get; set; }

    [MaxLength(120)]
    public string? DisplayName { get; set; }

    /// <summary>Master gate - when false the listener and the capture ignore this channel entirely.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Subscribe the EventSub chat types (<c>channel.chat.*</c>) for this channel.</summary>
    public bool CaptureChat { get; set; } = true;

    /// <summary>Capture the live audio (<c>audio_only</c> rendition) while the channel streams - for the Twitch transcriber. Gated by the capture service's global switch.</summary>
    public bool CaptureAudio { get; set; }

    /// <summary>Last time the login was resolved against Helix. Null = never verified.</summary>
    public DateTime? LastResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The Twitch account whose user token the listener uses for EventSub (chat subscriptions need
/// <c>user:read:chat</c> from the <i>chatting</i> user, i.e. this bot - no broadcaster consent). One row is
/// expected; connected from <c>/admin/twitch</c> via the authorization-code flow. Tokens are stored like
/// the Kick broadcaster tokens (plain columns in the gateway's own database).
/// </summary>
public class TwitchBotAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(64)]
    public string UserId { get; set; } = "";

    [MaxLength(64)]
    public string Login { get; set; } = "";

    [MaxLength(120)]
    public string DisplayName { get; set; } = "";

    [MaxLength(4000)]
    public string AccessToken { get; set; } = "";

    [MaxLength(4000)]
    public string RefreshToken { get; set; } = "";

    /// <summary>Space-separated scopes Twitch granted.</summary>
    [MaxLength(1000)]
    public string Scopes { get; set; } = "";

    public DateTime AccessTokenExpiresAt { get; set; }

    public DateTime? LastRefreshedAt { get; set; }

    /// <summary>Last successful <c>/oauth2/validate</c> (Twitch asks for one per hour).</summary>
    public DateTime? LastValidatedAt { get; set; }

    /// <summary>Set when a refresh or validation failed for good (revoked, password changed) - the admin must reconnect.</summary>
    [MaxLength(500)]
    public string? LastError { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Inbox/idempotency record per Twitch event the listener ingests - the Twitch sibling of
/// <see cref="ReceivedRealtimeEvent"/>. Insert keyed on <see cref="DedupeKey"/> and publish through the EF
/// outbox in the same transaction. EventSub may redeliver a notification after a reconnect; the duplicate
/// insert fails fast and is skipped without re-publishing. The chat-analytics projector tails this table.
/// </summary>
public class ReceivedTwitchEvent
{
    /// <summary>Primary key: <c>es|&lt;EventSub message_id&gt;</c> or <c>helix|...</c> for polled state.</summary>
    [MaxLength(200)]
    public string DedupeKey { get; set; } = "";

    /// <summary>EventSub subscription type (<c>channel.chat.message</c>) or synthetic <c>helix.stream.online</c> etc.</summary>
    [MaxLength(80)]
    public string EventType { get; set; } = "";

    [MaxLength(16)]
    public string EventVersion { get; set; } = "";

    [MaxLength(64)]
    public string BroadcasterUserId { get; set; } = "";

    /// <summary>Broadcaster login (lowercase).</summary>
    [MaxLength(64)]
    public string BroadcasterLogin { get; set; } = "";

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>EventSub <c>message_timestamp</c> (Twitch's clock).</summary>
    public DateTime? TwitchTimestamp { get; set; }

    public DateTime? PublishedAt { get; set; }

    /// <summary>Raw JSON of <c>payload.event</c> (or the Helix item for synthetic events).</summary>
    public string RawData { get; set; } = "";
}
