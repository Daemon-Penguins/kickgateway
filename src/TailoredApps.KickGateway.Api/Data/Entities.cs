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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the gateway successfully called /oauth/token with this refresh_token.</summary>
    public DateTime? LastRefreshedAt { get; set; }

    public List<KickEventSubscription> Subscriptions { get; set; } = new();
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
