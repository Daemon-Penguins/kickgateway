namespace TailoredApps.Integrations.Kick;

/// <summary>
/// Per-client OAuth + API configuration. The gateway runs N of these (one per
/// Kick developer app); pick by id from a registry rather than binding a single
/// instance from configuration.
/// </summary>
public class KickOptions
{
    public const string SectionName = "Kick";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Where Kick redirects after authorization. MUST be registered in the dev portal for this client.</summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>Space-separated scopes. Default covers everything the gateway needs.</summary>
    public string Scopes { get; set; } = "events:subscribe channel:read channel:write user:read chat:write streamkey:read moderation:ban";

    public string AuthBaseUrl { get; set; } = "https://id.kick.com";
    public string ApiBaseUrl { get; set; } = "https://api.kick.com";

    /// <summary>Public URL Kick should POST event webhooks to — set in the Kick dev portal, surfaced here for diagnostics.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>How long the gateway should cache the RSA public key used for signature verification.</summary>
    public int PublicKeyCacheMinutes { get; set; } = 60;
}

/// <summary>Static snapshot used to bind shared defaults (AuthBaseUrl / ApiBaseUrl / PublicKeyCacheMinutes / WebhookUrl).</summary>
public class KickGlobalDefaults
{
    public const string SectionName = "Kick";
    public string AuthBaseUrl { get; set; } = "https://id.kick.com";
    public string ApiBaseUrl { get; set; } = "https://api.kick.com";
    public string WebhookUrl { get; set; } = "";
    public int PublicKeyCacheMinutes { get; set; } = 60;
    public string DefaultScopes { get; set; } = "events:subscribe channel:read channel:write user:read chat:write streamkey:read moderation:ban";
}
