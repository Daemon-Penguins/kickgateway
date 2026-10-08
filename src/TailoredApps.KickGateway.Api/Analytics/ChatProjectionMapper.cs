using System.Text.Json;
using System.Text.RegularExpressions;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>Rows derived from one inbox row (webhook delivery or realtime frame).</summary>
public sealed record ChatProjection(IReadOnlyList<ChatMessageRecord> Messages, IReadOnlyList<ChatterEvent> Events)
{
    public static readonly ChatProjection Empty = new(Array.Empty<ChatMessageRecord>(), Array.Empty<ChatterEvent>());
}

/// <summary>
/// Pure mapper: a <see cref="ReceivedWebhook"/> (verbatim Kick JSON in <c>RawBody</c>) → chat
/// read-model rows. Parses defensively and accepts both the documented payload shape and the
/// older field names the dispatcher reads (e.g. <c>giftees</c>/<c>recipients</c>,
/// <c>redeemer</c>/<c>user</c>), since Kick payloads drift. Anything unparseable yields nothing.
/// </summary>
public static partial class ChatProjectionMapper
{
    public const int MaxContentLength = 2000;

    /// <summary>Webhook event types the projector reads; everything else is skipped at the query.</summary>
    public static readonly string[] ProjectedEventTypes =
    {
        KickEventTypes.ChatMessageSent,
        KickEventTypes.ChannelFollowed,
        KickEventTypes.SubscriptionNew,
        KickEventTypes.SubscriptionRenewal,
        KickEventTypes.SubscriptionGifts,
        KickEventTypes.KicksGifted,
        KickEventTypes.ChannelRewardRedemptionUpdated,
        KickEventTypes.ModerationBanned,
    };

    // Kick usernames: letters, digits, underscore. Not preceded by a word char so e-mail
    // addresses ("a@b.com") don't count.
    [GeneratedRegex(@"(?<![\w@])@([A-Za-z0-9_]{2,30})")]
    private static partial Regex MentionRegex();

    /// <param name="row">Inbox row.</param>
    /// <param name="knownSlug">Slug of <see cref="ReceivedWebhook.BroadcasterAccountId"/>, used when the payload lacks one.</param>
    public static ChatProjection Map(ReceivedWebhook row, string? knownSlug)
    {
        if (string.IsNullOrWhiteSpace(row.RawBody)) return ChatProjection.Empty;
        try
        {
            using var doc = JsonDocument.Parse(row.RawBody);
            var p = doc.RootElement;
            if (p.ValueKind != JsonValueKind.Object) return ChatProjection.Empty;

            var broadcaster = Obj(p, "broadcaster");
            var slug = (Str(broadcaster, "channel_slug") is { Length: > 0 } s ? s : knownSlug ?? "").ToLowerInvariant();
            if (slug.Length == 0) return ChatProjection.Empty;

            var ctx = new Ctx(row, slug, Str(broadcaster, "user_id"));
            return row.EventType switch
            {
                KickEventTypes.ChatMessageSent => MapChat(p, ctx),
                KickEventTypes.ChannelFollowed => One(MapUserEvent(p, ctx, "follower", ChatterEventKind.Follow)),
                KickEventTypes.SubscriptionNew => One(MapSub(p, ctx, ChatterEventKind.SubscriptionNew)),
                KickEventTypes.SubscriptionRenewal => One(MapSub(p, ctx, ChatterEventKind.SubscriptionRenewal)),
                KickEventTypes.SubscriptionGifts => new(Array.Empty<ChatMessageRecord>(), MapGifts(p, ctx)),
                KickEventTypes.KicksGifted => One(MapKicks(p, ctx)),
                KickEventTypes.ChannelRewardRedemptionUpdated => One(MapReward(p, ctx)),
                KickEventTypes.ModerationBanned => One(MapBan(p, ctx)),
                _ => ChatProjection.Empty,
            };
        }
        catch (JsonException)
        {
            return ChatProjection.Empty;
        }
    }

    /// <summary>Distinct lowercase <c>@mentions</c> in a message, excluding the sender.</summary>
    public static IReadOnlyList<string> ExtractMentions(string content, string? senderUsername = null)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains('@')) return Array.Empty<string>();
        var self = senderUsername?.ToLowerInvariant();
        var set = new List<string>();
        foreach (Match m in MentionRegex().Matches(content))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            if (name != self && !set.Contains(name)) set.Add(name);
        }
        return set;
    }

    // === per-event ===

    private sealed record Ctx(ReceivedWebhook Row, string Slug, string BroadcasterUserId);

    private static ChatProjection One(ChatterEvent? e) =>
        e is null ? ChatProjection.Empty : new(Array.Empty<ChatMessageRecord>(), new[] { e });

    private static ChatProjection MapChat(JsonElement p, Ctx c)
    {
        var id = Str(p, "message_id");
        var sender = Obj(p, "sender");
        var senderId = Str(sender, "user_id");
        if (id.Length == 0 || senderId.Length == 0) return ChatProjection.Empty;

        var identity = Obj(sender, "identity");
        var reply = Obj(p, "replies_to");
        var replySender = Obj(reply, "sender");
        var content = Str(p, "content");
        var username = Str(sender, "username");

        var msg = new ChatMessageRecord
        {
            MessageId = Cap(id, 64),
            Source = ChatSource.Webhook,
            SourceId = c.Row.MessageId,
            BroadcasterAccountId = c.Row.BroadcasterAccountId,
            BroadcasterUserId = Cap(c.BroadcasterUserId, 64),
            ChannelSlug = Cap(c.Slug, 120),
            SenderUserId = Cap(senderId, 64),
            SenderUsername = Cap(username, 120),
            SenderChannelSlug = NCap(Str(sender, "channel_slug"), 120),
            SenderIsVerified = Bool(sender, "is_verified"),
            SenderColor = NCap(Str(identity, "username_color"), 32),
            SenderBadges = NCap(ChatBadges.Encode(identity), 1000),
            Content = Cap(content, MaxContentLength),
            CreatedAt = Date(p, "created_at") ?? c.Row.ReceivedAt,
            ReplyToMessageId = NCap(Str(reply, "message_id"), 64),
            ReplyToUserId = NCap(Str(replySender, "user_id"), 64),
            ReplyToUsername = NCap(Str(replySender, "username"), 120),
        };
        foreach (var m in ExtractMentions(content, username))
            msg.Mentions.Add(new ChatMention { MessageId = msg.MessageId, MentionedUsername = Cap(m, 120) });

        return new(new[] { msg }, Array.Empty<ChatterEvent>());
    }

    private static ChatterEvent? MapUserEvent(JsonElement p, Ctx c, string userProp, ChatterEventKind kind)
    {
        var user = Obj(p, userProp);
        var uid = Str(user, "user_id");
        if (uid.Length == 0) return null;
        return Event(c, kind, "", uid, Str(user, "username"), Date(p, "created_at"));
    }

    private static ChatterEvent? MapSub(JsonElement p, Ctx c, ChatterEventKind kind)
    {
        var e = MapUserEvent(p, c, "subscriber", kind);
        if (e is null) return null;
        // Subscription payloads' created_at is when the SUBSCRIPTION started (every monthly renewal repeats
        // the original date), not when this event happened — the delivery time is the event time.
        e.OccurredAt = c.Row.ReceivedAt;
        e.Amount = Int(p, "duration");
        e.ExpiresAt = Date(p, "expires_at");
        return e;
    }

    private static IReadOnlyList<ChatterEvent> MapGifts(JsonElement p, Ctx c)
    {
        var gifter = Obj(p, "gifter");
        var gifterId = Str(gifter, "user_id");
        var gifterName = Bool(gifter, "is_anonymous") ? "" : Str(gifter, "username");
        DateTime? at = null; // delivery time — created_at belongs to the subscription, not the gift (see MapSub)
        var tier = Str(p, "tier");
        var giftees = Arr(p, "giftees") ?? Arr(p, "recipients");

        var list = new List<ChatterEvent>();
        if (giftees is { } arr)
        {
            var i = 0;
            foreach (var g in arr)
            {
                var e = Event(c, ChatterEventKind.SubscriptionGift, $"gift:{i++}", gifterId, gifterName, at);
                e.CounterpartUserId = NCap(Str(g, "user_id"), 64);
                e.CounterpartUsername = NCap(Str(g, "username"), 120);
                e.Amount = 1;
                e.Detail = NCap(tier, 500);
                e.ExpiresAt = Date(p, "expires_at");
                list.Add(e);
            }
        }
        if (list.Count == 0)
        {
            // No giftee list — keep the gift itself (count unknown → gift_count or 1).
            var e = Event(c, ChatterEventKind.SubscriptionGift, "gift", gifterId, gifterName, at);
            e.Amount = Math.Max(1, Int(p, "gift_count"));
            e.Detail = NCap(tier, 500);
            list.Add(e);
        }
        return list;
    }

    private static ChatterEvent? MapKicks(JsonElement p, Ctx c)
    {
        var sender = Obj(p, "sender") is { ValueKind: JsonValueKind.Object } s ? s : Obj(p, "gifter");
        var gift = Obj(p, "gift");
        var uid = Str(sender, "user_id");
        if (uid.Length == 0) return null;
        var e = Event(c, ChatterEventKind.KicksGift, "", uid, Str(sender, "username"), Date(p, "created_at"));
        e.Amount = gift.ValueKind == JsonValueKind.Object ? Int(gift, "amount") : Int(p, "amount");
        var message = gift.ValueKind == JsonValueKind.Object ? Str(gift, "message") : Str(p, "message");
        e.Detail = NCap(message, 500);
        return e;
    }

    private static ChatterEvent? MapReward(JsonElement p, Ctx c)
    {
        var user = Obj(p, "redeemer") is { ValueKind: JsonValueKind.Object } r ? r : Obj(p, "user");
        var uid = Str(user, "user_id");
        if (uid.Length == 0) return null;
        var reward = Obj(p, "reward");
        var e = Event(c, ChatterEventKind.RewardRedemption, "", uid, Str(user, "username"), Date(p, "redeemed_at"));
        e.Amount = Int(reward, "cost");
        e.Detail = NCap(Str(reward, "title"), 500);
        e.RefId = NCap(Str(p, "id") is { Length: > 0 } id ? id : Str(p, "redemption_id"), 120);
        return e;
    }

    private static ChatterEvent? MapBan(JsonElement p, Ctx c)
    {
        var banned = Obj(p, "banned_user");
        var uid = Str(banned, "user_id");
        if (uid.Length == 0) return null;
        var mod = Obj(p, "moderator");
        var meta = Obj(p, "metadata");
        var expires = Date(meta, "expires_at");
        var e = Event(c, expires is null ? ChatterEventKind.Ban : ChatterEventKind.Timeout, "",
            uid, Str(banned, "username"), Date(meta, "created_at") ?? Date(p, "banned_at"));
        e.CounterpartUserId = NCap(Str(mod, "user_id"), 64);
        e.CounterpartUsername = NCap(Str(mod, "username"), 120);
        e.Detail = NCap(Str(meta, "reason"), 500);
        e.ExpiresAt = expires;
        return e;
    }

    private static ChatterEvent Event(Ctx c, ChatterEventKind kind, string suffix, string userId, string username, DateTime? at) => new()
    {
        Id = suffix.Length == 0 ? c.Row.MessageId : $"{c.Row.MessageId}:{suffix}",
        Source = ChatSource.Webhook,
        SourceId = c.Row.MessageId,
        Kind = kind,
        BroadcasterAccountId = c.Row.BroadcasterAccountId,
        ChannelSlug = Cap(c.Slug, 120),
        UserId = Cap(userId, 64),
        Username = Cap(username, 120),
        OccurredAt = at ?? c.Row.ReceivedAt,
    };

    // === JSON helpers (defensive; Kick payload shapes drift) ===

    internal static string Cap(string s, int max) => s.Length <= max ? s : s[..max];
    internal static string? NCap(string? s, int max) => string.IsNullOrEmpty(s) ? null : Cap(s, max);

    internal static JsonElement Obj(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    internal static JsonElement.ArrayEnumerator? Arr(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : null;

    internal static string Str(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => "",
        };
    }

    internal static int Int(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => 0,
        };
    }

    internal static bool Bool(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    internal static DateTime? Date(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var dt))
            return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return null;
    }
}
