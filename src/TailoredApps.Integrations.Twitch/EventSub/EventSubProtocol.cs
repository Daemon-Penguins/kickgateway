using System.Text.Json;

namespace TailoredApps.Integrations.Twitch.EventSub;

/// <summary><c>payload.session</c> of a welcome / reconnect message.</summary>
public sealed record EventSubSession(string Id, string Status, int? KeepaliveTimeoutSeconds, string? ReconnectUrl, DateTime? ConnectedAt);

/// <summary>
/// One decoded EventSub WebSocket message. <see cref="EventJson"/> is the raw <c>payload.event</c>
/// object (notifications only); <see cref="SubscriptionJson"/> the raw <c>payload.subscription</c>.
/// </summary>
public sealed record EventSubMessage(
    string MessageId,
    string MessageType,
    DateTime Timestamp,
    string? SubscriptionType,
    string? SubscriptionVersion,
    EventSubSession? Session,
    string? SubscriptionId,
    string? SubscriptionStatus,
    string? SubscriptionJson,
    string? EventJson)
{
    public bool IsWelcome => MessageType == EventSubProtocol.Welcome;
    public bool IsKeepalive => MessageType == EventSubProtocol.Keepalive;
    public bool IsNotification => MessageType == EventSubProtocol.Notification;
    public bool IsReconnect => MessageType == EventSubProtocol.Reconnect;
    public bool IsRevocation => MessageType == EventSubProtocol.Revocation;
}

/// <summary>The EventSub WebSocket message shapes (dev.twitch.tv/docs/eventsub/handling-websocket-events).</summary>
public static class EventSubProtocol
{
    public const string Welcome = "session_welcome";
    public const string Keepalive = "session_keepalive";
    public const string Notification = "notification";
    public const string Reconnect = "session_reconnect";
    public const string Revocation = "revocation";

    /// <summary>Seconds the client has after a welcome to create its first subscription before Twitch closes the socket (4003).</summary>
    public const int SubscribeWindowSeconds = 10;

    /// <summary>Twitch limits: 3 connections with enabled subscriptions per user token, 300 enabled subscriptions per connection.</summary>
    public const int MaxConnectionsPerToken = 3;
    public const int MaxSubscriptionsPerConnection = 300;

    /// <summary>Null when the text is not an EventSub message (no <c>metadata.message_type</c>).</summary>
    public static EventSubMessage? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var metadata = Obj(root, "metadata");
        var type = Str(metadata, "message_type");
        if (type.Length == 0) return null;
        var payload = Obj(root, "payload");

        EventSubSession? session = null;
        var s = Obj(payload, "session");
        if (s.ValueKind == JsonValueKind.Object)
        {
            session = new EventSubSession(
                Str(s, "id"), Str(s, "status"),
                s.TryGetProperty("keepalive_timeout_seconds", out var ka) && ka.ValueKind == JsonValueKind.Number && ka.TryGetInt32(out var kas) ? kas : null,
                NullIfEmpty(Str(s, "reconnect_url")),
                Date(s, "connected_at"));
        }

        var sub = Obj(payload, "subscription");
        var ev = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;

        return new EventSubMessage(
            Str(metadata, "message_id"),
            type,
            Date(metadata, "message_timestamp") ?? DateTime.UtcNow,
            NullIfEmpty(Str(metadata, "subscription_type")),
            NullIfEmpty(Str(metadata, "subscription_version")),
            session,
            sub.ValueKind == JsonValueKind.Object ? NullIfEmpty(Str(sub, "id")) : null,
            sub.ValueKind == JsonValueKind.Object ? NullIfEmpty(Str(sub, "status")) : null,
            sub.ValueKind == JsonValueKind.Object ? sub.GetRawText() : null,
            ev.ValueKind == JsonValueKind.Object ? ev.GetRawText() : null);
    }

    /// <summary>Appends <c>keepalive_timeout_seconds</c> (10..600) to the WebSocket URL.</summary>
    public static string WithKeepalive(string url, int keepaliveSeconds)
    {
        var k = Math.Clamp(keepaliveSeconds, 10, 600);
        return url.Contains('?') ? $"{url}&keepalive_timeout_seconds={k}" : $"{url}?keepalive_timeout_seconds={k}";
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static JsonElement Obj(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTime? Date(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) return null;
        // Twitch stamps nanosecond precision ("2026-10-15T12:34:56.123456789Z"); DateTime parses up to 7 digits, so trim.
        var text = v.GetString() ?? "";
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        var dot = text.IndexOf('.');
        if (dot > 0 && text.EndsWith('Z') && text.Length - dot > 8)
        {
            var trimmed = text[..(dot + 8)] + "Z";
            if (DateTime.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
                return dt;
        }
        return null;
    }
}

/// <summary>EventSub subscription types the listener uses and the condition each needs.</summary>
public static class EventSubTypes
{
    public const string ChatMessage = "channel.chat.message";
    public const string ChatNotification = "channel.chat.notification";
    public const string ChatMessageDelete = "channel.chat.message_delete";
    public const string ChatClear = "channel.chat.clear";
    public const string ChatClearUserMessages = "channel.chat.clear_user_messages";
    public const string ChatSettingsUpdate = "channel.chat_settings.update";

    /// <summary>Every chat type is version 1 and takes (<c>broadcaster_user_id</c>, <c>user_id</c> = the bot / chatting user).</summary>
    public static IReadOnlyDictionary<string, string> ChatCondition(string broadcasterUserId, string botUserId) =>
        new Dictionary<string, string> { ["broadcaster_user_id"] = broadcasterUserId, ["user_id"] = botUserId };
}
