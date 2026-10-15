namespace TailoredApps.TwitchGateway.Listener;

/// <summary>Configuration for the Twitch listener, bound from the <c>Twitch:Listener</c> section.</summary>
public class TwitchListenerOptions
{
    public const string SectionName = "Twitch:Listener";

    // === roster ===

    /// <summary>How often the roster (TwitchChannels) is re-read and the EventSub subscriptions reconciled.</summary>
    public int RosterRefreshSeconds { get; set; } = 30;

    // === EventSub sessions ===

    /// <summary><c>keepalive_timeout_seconds</c> asked of Twitch (10..600). A silent socket past this + grace is reconnected.</summary>
    public int KeepaliveTimeoutSeconds { get; set; } = 30;

    /// <summary>Subscriptions per WebSocket session before the next channel goes to another session (Twitch allows 300).</summary>
    public int MaxSubscriptionsPerSession { get; set; } = 280;

    /// <summary>WebSocket sessions with enabled subscriptions (Twitch allows 3 per user token).</summary>
    public int MaxSessions { get; set; } = 3;

    public int ReconnectMinSeconds { get; set; } = 1;
    public int ReconnectMaxSeconds { get; set; } = 60;

    /// <summary>Which chat subscription types to create per channel (all cost 0 on the bot's user token).</summary>
    public bool SubscribeChatMessages { get; set; } = true;
    public bool SubscribeChatNotifications { get; set; } = true;
    public bool SubscribeChatMessageDeletes { get; set; } = true;
    public bool SubscribeChatClears { get; set; } = true;
    public bool SubscribeChatSettings { get; set; } = false;

    // === stream state (Helix polling) ===

    /// <summary>How often <c>GET /streams</c> is polled for every enabled channel (0 = off). One request covers 100 channels.</summary>
    public int StreamPollSeconds { get; set; } = 30;

    // === bot token ===

    /// <summary>Twitch requires an hourly <c>/oauth2/validate</c>; done this often while the listener runs.</summary>
    public int TokenValidateMinutes { get; set; } = 55;

    // === ingest (event -> inbox + outbox) ===

    public int IngestWorkers { get; set; } = 4;
    public int IngestQueueCapacity { get; set; } = 1000;
    public int IngestRetries { get; set; } = 2;
    public int IngestRetryDelayMs { get; set; } = 250;

    /// <summary>The enabled chat types in a stable order.</summary>
    public IReadOnlyList<string> EnabledChatTypes()
    {
        var list = new List<string>(5);
        if (SubscribeChatMessages) list.Add(Integrations.Twitch.EventSub.EventSubTypes.ChatMessage);
        if (SubscribeChatNotifications) list.Add(Integrations.Twitch.EventSub.EventSubTypes.ChatNotification);
        if (SubscribeChatMessageDeletes) list.Add(Integrations.Twitch.EventSub.EventSubTypes.ChatMessageDelete);
        if (SubscribeChatClears)
        {
            list.Add(Integrations.Twitch.EventSub.EventSubTypes.ChatClear);
            list.Add(Integrations.Twitch.EventSub.EventSubTypes.ChatClearUserMessages);
        }
        if (SubscribeChatSettings) list.Add(Integrations.Twitch.EventSub.EventSubTypes.ChatSettingsUpdate);
        return list;
    }
}
