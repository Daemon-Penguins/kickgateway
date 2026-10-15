using System.Text.Json;
using TailoredApps.Integrations.Twitch.EventSub;
using TailoredApps.KickGateway.Api.Data;
using static TailoredApps.KickGateway.Api.Analytics.ChatProjectionMapper;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>
/// Pure mapper: a <see cref="ReceivedTwitchEvent"/> (EventSub <c>payload.event</c> JSON in <c>RawData</c>) -> chat
/// read-model rows with <c>Platform = twitch</c>. Mirrors the field knowledge of the Twitch listener's
/// <c>TwitchEventMapper</c>, but produces analytics rows instead of bus contracts.
/// <para>
/// Mapping onto the Kick-shaped kinds: a cheer (bits on a message) is a <see cref="ChatterEventKind.KicksGift"/>
/// (Amount = bits, Detail = "bits"); sub / resub / sub_gift notices are the subscription kinds (tier in Detail);
/// <c>message_delete</c> is <see cref="ChatterEventKind.MessageDeleted"/>; <c>clear_user_messages</c> (Twitch's chat-side
/// view of a ban or timeout, undistinguished) is a <see cref="ChatterEventKind.Timeout"/> with Detail
/// "clear_user_messages". Raids, announcements, charity, watch streaks and stream state are not chatter events
/// and are skipped here (they are still on the bus and in the inbox).
/// </para>
/// </summary>
public static class TwitchChatProjectionMapper
{
    public static ChatProjection Map(ReceivedTwitchEvent row)
    {
        var login = (row.BroadcasterLogin ?? "").Trim().ToLowerInvariant();
        if (login.Length == 0 || string.IsNullOrWhiteSpace(row.RawData)) return ChatProjection.Empty;
        try
        {
            using var doc = JsonDocument.Parse(row.RawData);
            var p = doc.RootElement;
            if (p.ValueKind != JsonValueKind.Object) return ChatProjection.Empty;

            return row.EventType switch
            {
                EventSubTypes.ChatMessage => MapChat(p, row, login),
                EventSubTypes.ChatNotification => MapNotification(p, row, login),
                EventSubTypes.ChatMessageDelete => One(MapDeleted(p, row, login)),
                EventSubTypes.ChatClearUserMessages => One(MapUserCleared(p, row, login)),
                _ => ChatProjection.Empty,
            };
        }
        catch (JsonException)
        {
            return ChatProjection.Empty;
        }
    }

    private static ChatProjection One(ChatterEvent? e) =>
        e is null ? ChatProjection.Empty : new(Array.Empty<ChatMessageRecord>(), new[] { e });

    private static ChatProjection MapChat(JsonElement p, ReceivedTwitchEvent row, string login)
    {
        var msg = Message(p, row, login, Str(p, "message_id"), Str(p, "chatter_user_id"), Str(p, "chatter_user_login"), Str(p, "chatter_user_name"));
        if (msg is null) return ChatProjection.Empty;

        var events = new List<ChatterEvent>();
        var cheer = Obj(p, "cheer");
        var bits = cheer.ValueKind == JsonValueKind.Object ? Int(cheer, "bits") : 0;
        if (bits > 0)
        {
            var e = Event(row, login, ChatterEventKind.KicksGift, "cheer", msg.SenderUserId, msg.SenderUsername, msg.CreatedAt);
            e.Amount = bits;
            e.Detail = NCap("bits: " + Str(Obj(p, "message"), "text"), 500);
            e.RefId = NCap(msg.MessageId, 120);
            events.Add(e);
        }
        return new(new[] { msg }, events);
    }

    private static ChatProjection MapNotification(JsonElement p, ReceivedTwitchEvent row, string login)
    {
        var notice = Str(p, "notice_type");
        var anonymous = Bool(p, "chatter_is_anonymous");
        var chatterId = anonymous ? "" : Str(p, "chatter_user_id");
        var chatterLogin = anonymous ? "" : Str(p, "chatter_user_login");
        var chatterName = anonymous ? "" : Str(p, "chatter_user_name");
        var at = row.TwitchTimestamp ?? row.ReceivedAt;
        var shared = notice.StartsWith("shared_chat_", StringComparison.Ordinal);
        JsonElement Part(string name) => Obj(p, shared ? "shared_chat_" + name : name) is { ValueKind: JsonValueKind.Object } x ? x : Obj(p, name);
        var kind = shared ? notice["shared_chat_".Length..] : notice;

        var messages = new List<ChatMessageRecord>();
        var events = new List<ChatterEvent>();

        // The user's own text (resub message, announcement body) is a chat line too.
        var text = Str(Obj(p, "message"), "text");
        if (text.Length > 0 && chatterId.Length > 0)
        {
            var msg = Message(p, row, login, Str(p, "message_id"), chatterId, chatterLogin, chatterName);
            if (msg is not null) messages.Add(msg);
        }

        switch (kind)
        {
            case "sub":
            {
                var sub = Part("sub");
                var e = UserEvent(row, login, ChatterEventKind.SubscriptionNew, "", chatterId, chatterLogin, at);
                if (e is null) break;
                e.Amount = Math.Max(1, Int(sub, "duration_months"));
                e.Detail = NCap(Tier(Str(sub, "sub_tier"), Bool(sub, "is_prime")), 500);
                events.Add(e);
                break;
            }
            case "resub":
            {
                var resub = Part("resub");
                var e = UserEvent(row, login, ChatterEventKind.SubscriptionRenewal, "", chatterId, chatterLogin, at);
                if (e is null) break;
                e.Amount = Math.Max(1, Int(resub, "cumulative_months"));
                e.Detail = NCap(Tier(Str(resub, "sub_tier"), Bool(resub, "is_prime")), 500);
                if (Bool(resub, "is_gift"))
                {
                    e.CounterpartUserId = NCap(Str(resub, "gifter_user_id"), 64);
                    e.CounterpartUsername = NCap(Str(resub, "gifter_user_login"), 120);
                }
                events.Add(e);
                break;
            }
            case "sub_gift":
            {
                var gift = Part("sub_gift");
                // Actor = gifter (empty id when anonymous), counterpart = recipient - the Kick gift shape.
                var e = Event(row, login, ChatterEventKind.SubscriptionGift, "", chatterId, chatterLogin, at);
                e.CounterpartUserId = NCap(Str(gift, "recipient_user_id"), 64);
                e.CounterpartUsername = NCap(Str(gift, "recipient_user_login"), 120);
                e.Amount = 1;
                e.Detail = NCap(Tier(Str(gift, "sub_tier"), false), 500);
                e.RefId = NCap(Str(gift, "community_gift_id"), 120);
                events.Add(e);
                break;
            }
            // community_sub_gift is the summary of a batch; each recipient arrives as its own sub_gift notice.
            default:
                break;
        }

        return new(messages, events);
    }

    private static ChatterEvent? MapDeleted(JsonElement p, ReceivedTwitchEvent row, string login)
    {
        var messageId = Str(p, "message_id");
        if (messageId.Length == 0) return null;
        var e = Event(row, login, ChatterEventKind.MessageDeleted, "", Str(p, "target_user_id"), Str(p, "target_user_login"), row.TwitchTimestamp ?? row.ReceivedAt);
        e.RefId = Cap(messageId, 120);
        return e;
    }

    private static ChatterEvent? MapUserCleared(JsonElement p, ReceivedTwitchEvent row, string login)
    {
        var e = UserEvent(row, login, ChatterEventKind.Timeout, "", Str(p, "target_user_id"), Str(p, "target_user_login"), row.TwitchTimestamp ?? row.ReceivedAt);
        if (e is null) return null;
        e.Detail = "clear_user_messages";
        return e;
    }

    private static ChatMessageRecord? Message(JsonElement p, ReceivedTwitchEvent row, string login, string id, string senderId, string senderLogin, string senderName)
    {
        if (id.Length == 0 || senderId.Length == 0) return null;
        var message = Obj(p, "message");
        var content = Str(message, "text");
        var reply = Obj(p, "reply");
        var badges = Badges(p);

        var msg = new ChatMessageRecord
        {
            MessageId = Cap(id, 64),
            Source = ChatSource.Twitch,
            SourceId = Cap(row.DedupeKey, 200),
            Platform = ChatPlatforms.Twitch,
            BroadcasterAccountId = null,
            BroadcasterUserId = Cap(row.BroadcasterUserId ?? "", 64),
            ChannelSlug = Cap(login, 120),
            SenderUserId = Cap(senderId, 64),
            SenderUsername = Cap(senderLogin.Length > 0 ? senderLogin : senderName, 120),
            SenderChannelSlug = NCap(senderLogin, 120),
            SenderIsVerified = badges.Any(b => b.SetId == "partner"),
            SenderColor = NCap(Str(p, "color"), 32),
            SenderBadges = NCap(EncodeBadges(badges), 1000),
            Content = Cap(content, MaxContentLength),
            CreatedAt = row.TwitchTimestamp ?? row.ReceivedAt,
            ReplyToMessageId = NCap(Str(reply, "parent_message_id"), 64),
            ReplyToUserId = NCap(Str(reply, "parent_user_id"), 64),
            ReplyToUsername = NCap(Str(reply, "parent_user_login"), 120),
        };

        // Mentions: the typed fragments first (exact logins), the @-regex as a fallback for plain text.
        var mentions = new List<string>();
        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("fragments", out var frags) && frags.ValueKind == JsonValueKind.Array)
            foreach (var f in frags.EnumerateArray())
            {
                var m = Obj(f, "mention");
                var l = Str(m, "user_login").ToLowerInvariant();
                if (l.Length > 0 && l != msg.SenderUsername.ToLowerInvariant() && !mentions.Contains(l)) mentions.Add(l);
            }
        foreach (var m in ExtractMentions(content, msg.SenderUsername))
            if (!mentions.Contains(m)) mentions.Add(m);
        foreach (var m in mentions)
            msg.Mentions.Add(new ChatMention { MessageId = msg.MessageId, MentionedUsername = Cap(m, 120) });
        return msg;
    }

    private readonly record struct Badge(string SetId, string Id, string Info);

    private static List<Badge> Badges(JsonElement p)
    {
        var list = new List<Badge>();
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("badges", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var b in arr.EnumerateArray())
            if (b.ValueKind == JsonValueKind.Object) list.Add(new Badge(Str(b, "set_id"), Str(b, "id"), Str(b, "info")));
        return list;
    }

    /// <summary>Kick's compact <c>type:count|type:count</c> shape (<c>ChatBadges</c> parses it): subscriber months from <c>info</c>, else the badge version.</summary>
    public static string EncodeBadges(IEnumerable<(string SetId, string Id, string Info)> badges) =>
        string.Join('|', badges.Where(b => b.SetId.Length > 0).Select(b =>
        {
            var n = int.TryParse(b.Info, out var i) ? i : int.TryParse(b.Id, out var v) ? v : 0;
            return $"{b.SetId}:{n}";
        }));

    private static string EncodeBadges(List<Badge> badges) => EncodeBadges(badges.Select(b => (b.SetId, b.Id, b.Info)));

    private static string Tier(string tier, bool prime) => prime ? "prime" : tier switch { "1000" => "tier1", "2000" => "tier2", "3000" => "tier3", _ => tier };

    private static ChatterEvent? UserEvent(ReceivedTwitchEvent row, string login, ChatterEventKind kind, string suffix, string userId, string username, DateTime? at)
        => userId.Length == 0 && username.Length == 0 ? null : Event(row, login, kind, suffix, userId, username, at);

    private static ChatterEvent Event(ReceivedTwitchEvent row, string login, ChatterEventKind kind, string suffix, string userId, string username, DateTime? at) => new()
    {
        Id = suffix.Length == 0 ? $"tw:{row.DedupeKey}" : $"tw:{row.DedupeKey}:{suffix}",
        Source = ChatSource.Twitch,
        SourceId = Cap(row.DedupeKey, 200),
        Platform = ChatPlatforms.Twitch,
        Kind = kind,
        BroadcasterAccountId = null,
        ChannelSlug = Cap(login, 120),
        UserId = Cap(userId, 64),
        Username = Cap(username, 120),
        OccurredAt = at ?? row.ReceivedAt,
    };
}
