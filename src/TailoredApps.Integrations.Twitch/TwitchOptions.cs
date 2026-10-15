namespace TailoredApps.Integrations.Twitch;

/// <summary>Configuration section <c>Twitch</c> - shared by the Api (bot OAuth), the listener and the audio capture.</summary>
public sealed class TwitchOptions
{
    public const string SectionName = "Twitch";

    /// <summary>Client id of the Twitch developer application (dev.twitch.tv/console/apps). Needed for OAuth, Helix and EventSub.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Client secret of that application (Confidential client type). Never logged.</summary>
    public string ClientSecret { get; set; } = "";

    public string AuthBaseUrl { get; set; } = "https://id.twitch.tv";

    public string HelixBaseUrl { get; set; } = "https://api.twitch.tv/helix";

    /// <summary>EventSub WebSocket endpoint; <c>keepalive_timeout_seconds</c> is appended by the listener.</summary>
    public string EventSubWebSocketUrl { get; set; } = "wss://eventsub.wss.twitch.tv/ws";

    /// <summary>Scopes the bot account authorizes. <c>user:read:chat</c> is what EventSub chat needs on a user token; <c>user:bot</c> is harmless and future-proofs app-token use.</summary>
    public string BotScopes { get; set; } = "user:read:chat user:bot";

    // --- live playback (what the web player does; no Twitch credentials involved) ---

    public string GqlUrl { get; set; } = "https://gql.twitch.tv/gql";

    /// <summary>Client id of Twitch's own web player - the one every browser sends to GQL for a playback token. Not a secret.</summary>
    public string GqlClientId { get; set; } = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    /// <summary>sha256 of the <c>PlaybackAccessToken</c> persisted GQL query (changes rarely; override when Twitch rotates it).</summary>
    public string PlaybackAccessTokenHash { get; set; } = "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9";

    public string UsherBaseUrl { get; set; } = "https://usher.ttvnw.net";

    /// <summary>User-Agent for playlist/segment requests (a browser, like Streamlink uses).</summary>
    public string PlaybackUserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>True when a developer application is configured (OAuth, Helix and EventSub can be used).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
