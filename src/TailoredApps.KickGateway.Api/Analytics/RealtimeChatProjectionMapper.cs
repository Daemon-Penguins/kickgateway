using System.Text.Json;
using TailoredApps.KickGateway.Api.Data;
using static TailoredApps.KickGateway.Api.Analytics.ChatProjectionMapper;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>
/// Pure mapper: a <see cref="ReceivedRealtimeEvent"/> (Pusher frame, inner JSON in <c>RawData</c>)
/// → chat read-model rows. Mirrors the field knowledge of the Realtime service's
/// <c>RealtimeFrameMapper</c>, but produces analytics rows instead of bus contracts.
///
/// Pusher payloads often carry only usernames (subs, gifts, follows); those rows come out with an
/// empty user id and are resolved to ids by <see cref="ChatProjector"/> where the username is known.
/// </summary>
public static class RealtimeChatProjectionMapper
{
    /// <param name="row">Realtime inbox row.</param>
    /// <param name="webhookCoversChannel">True when the channel also has an enabled webhook broadcaster.
    /// Webhooks are then authoritative for subs/gifts/kicks/rewards/follows/bans, and only the
    /// realtime-only kinds (chat, message deletions, unbans) are taken from the Pusher frame — chat
    /// itself is deduplicated on Kick's message id.</param>
    public static ChatProjection Map(ReceivedRealtimeEvent row, bool webhookCoversChannel)
    {
        var slug = (row.Slug ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0 || string.IsNullOrWhiteSpace(row.RawData)) return ChatProjection.Empty;
        try
        {
            using var doc = JsonDocument.Parse(row.RawData);
            var p = doc.RootElement;
            if (p.ValueKind != JsonValueKind.Object) return ChatProjection.Empty;

            var name = ShortName(row.EventName);
            switch (name)
            {
                case "ChatMessageEvent": return MapChat(p, row, slug);
                case "MessageDeletedEvent": return One(MapDeleted(p, row, slug));
                case "UserUnbannedEvent": return One(MapUnbanned(p, row, slug));
            }
            if (webhookCoversChannel) return ChatProjection.Empty;

            return name switch
            {
                "UserBannedEvent" => One(MapBanned(p, row, slug)),
                "SubscriptionEvent" or "ChannelSubscriptionEvent" => One(MapSubscription(p, row, slug)),
                "GiftedSubscriptionsEvent" => new(Array.Empty<ChatMessageRecord>(), MapGifts(p, row, slug)),
                "KicksGifted" => One(MapKicks(p, row, slug)),
                "RewardRedeemedEvent" => One(MapReward(p, row, slug)),
                "FollowersUpdated" => One(MapFollow(p, row, slug)),
                _ => ChatProjection.Empty,
            };
        }
        catch (JsonException)
        {
            return ChatProjection.Empty;
        }
    }

    /// <summary><c>App\Events\ChatMessageEvent</c> → <c>ChatMessageEvent</c>.</summary>
    public static string ShortName(string? evt)
    {
        if (string.IsNullOrEmpty(evt)) return "";
        var idx = evt.LastIndexOf('\\');
        return idx >= 0 && idx < evt.Length - 1 ? evt[(idx + 1)..] : evt;
    }

    private static ChatProjection One(ChatterEvent? e) =>
        e is null ? ChatProjection.Empty : new(Array.Empty<ChatMessageRecord>(), new[] { e });

    private static ChatProjection MapChat(JsonElement p, ReceivedRealtimeEvent row, string slug)
    {
        var id = Str(p, "id");
        var sender = Obj(p, "sender");
        var senderId = Str(sender, "id");
        if (id.Length == 0 || senderId.Length == 0) return ChatProjection.Empty;

        var identity = Obj(sender, "identity");
        var meta = Obj(p, "metadata");
        var origMsg = Obj(meta, "original_message");
        var origSender = Obj(meta, "original_sender");
        var content = Str(p, "content");
        var username = Str(sender, "username");

        var msg = new ChatMessageRecord
        {
            MessageId = Cap(id, 64),
            Source = ChatSource.Realtime,
            SourceId = Cap(row.DedupeKey, 200),
            BroadcasterAccountId = null,
            BroadcasterUserId = "",
            ChannelSlug = Cap(slug, 120),
            SenderUserId = Cap(senderId, 64),
            SenderUsername = Cap(username, 120),
            SenderChannelSlug = NCap(Str(sender, "slug"), 120),
            SenderIsVerified = Bool(sender, "is_verified"),
            SenderColor = NCap(Str(identity, "color") is { Length: > 0 } c ? c : Str(identity, "username_color"), 32),
            SenderBadges = NCap(ChatBadges.Encode(identity), 1000),
            Content = Cap(content, MaxContentLength),
            CreatedAt = Date(p, "created_at") ?? row.ReceivedAt,
            ReplyToMessageId = NCap(Str(origMsg, "id"), 64),
            ReplyToUserId = NCap(Str(origSender, "id"), 64),
            ReplyToUsername = NCap(Str(origSender, "username"), 120),
        };
        foreach (var m in ExtractMentions(content, username))
            msg.Mentions.Add(new ChatMention { MessageId = msg.MessageId, MentionedUsername = Cap(m, 120) });

        return new(new[] { msg }, Array.Empty<ChatterEvent>());
    }

    private static ChatterEvent? MapDeleted(JsonElement p, ReceivedRealtimeEvent row, string slug)
    {
        var msg = Obj(p, "message");
        var messageId = msg.ValueKind == JsonValueKind.Object ? Str(msg, "id") : Str(p, "id");
        if (messageId.Length == 0) return null;
        // Author is filled in by the projector from the stored message (the frame doesn't carry it).
        var e = Event(row, slug, ChatterEventKind.MessageDeleted, "", "", "", null);
        e.RefId = Cap(messageId, 120);
        e.Detail = Bool(p, "aiModerated") || Bool(p, "ai_moderated") ? "ai" : null;
        return e;
    }

    private static ChatterEvent? MapUnbanned(JsonElement p, ReceivedRealtimeEvent row, string slug)
    {
        var user = Obj(p, "user");
        var by = Obj(p, "unbanned_by");
        var e = UserEvent(row, slug, ChatterEventKind.Unban, "", Str(user, "id"), Str(user, "username"), Date(p, "created_at"));
        if (e is null) return null;
        e.CounterpartUserId = NCap(Str(by, "id"), 64);
        e.CounterpartUsername = NCap(Str(by, "username"), 120);
        return e;
    }

    private static ChatterEvent? MapBanned(JsonElement p, ReceivedRealtimeEvent row, string slug)
    {
        var user = Obj(p, "user");
        var by = Obj(p, "banned_by");
        var expires = Date(p, "expires_at");
        var permanent = Bool(p, "permanent") || (expires is null && !p.TryGetProperty("expires_at", out _));
        var e = UserEvent(row, slug, permanent ? ChatterEventKind.Ban : ChatterEventKind.Timeout, "",
            Str(user, "id"), Str(user, "username"), Date(p, "created_at"));
        if (e is null) return null;
        e.CounterpartUserId = NCap(Str(by, "id"), 64);
        e.CounterpartUsername = NCap(Str(by, "username"), 120);
        e.Amount = Int(p, "duration");
        e.ExpiresAt = expires;
        return e;
    }

    private static ChatterEvent? MapSubscription(JsonElement p, ReceivedRealtimeEvent row, string slug)
    {
        var months = Int(p, "months");
        var e = UserEvent(row, slug, months > 1 ? ChatterEventKind.SubscriptionRenewal : ChatterEventKind.SubscriptionNew, "",
            Str(p, "user_id"), Str(p, "username"), Date(p, "created_at"));
        if (e is null) return null;
        e.Amount = months;
        return e;
    }

    private static IReadOnlyList<ChatterEvent> MapGifts(JsonElement p, ReceivedRealtimeEvent row, string slug)
    {
        var gifter = Str(p, "gifter_username");
        var at = Date(p, "created_at");
        var list = new List<ChatterEvent>();
        if (Arr(p, "gifted_usernames") is { } arr)
        {
            var i = 0;
            foreach (var g in arr)
            {
                if (g.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(g.GetString())) continue;
                var e = Event(row, slug, ChatterEventKind.SubscriptionGift, $"gift:{i++}", "", gifter, at);
                e.CounterpartUsername = Cap(g.GetString()!, 120);
                e.Amount = 1;
                list.Add(e);
            }
        }
        return list;
    }

    private static ChatterEvent? MapKicks(JsonElement p, ReceivedRealtimeEvent row, string slug)
    {
        var sender = Obj(p, "sender");
        var gift = Obj(p, "gift");
        var e = UserEvent(row, slug, ChatterEventKind.KicksGift, "", Str(sender, "id"), Str(sender, "username"), Date(p, "created_at"));
        if (e is null) return null;
        e.Amount = Int(gift, "amount");
        e.Detail = NCap(Str(p, "message") is { Length: > 0 } m ? m : Str(gift, "name"), 500);
        return e;
    }

    private static ChatterEvent? MapReward(JsonElement p, ReceivedRealtimeEvent row, string slug)
    {
        var e = UserEvent(row, slug, ChatterEventKind.RewardRedemption, "", Str(p, "user_id"), Str(p, "username"), Date(p, "created_at"));
        if (e is null) return null;
        e.Detail = NCap(Str(p, "reward_title"), 500);
        e.RefId = Cap(row.DedupeKey, 120);
        return e;
    }

    private static ChatterEvent? MapFollow(JsonElement p, ReceivedRealtimeEvent row, string slug)
    {
        // FollowersUpdated fires for unfollows too and often without a username — only an explicit
        // "followed: true" with a username is a follow we can attribute.
        if (!Bool(p, "followed")) return null;
        return UserEvent(row, slug, ChatterEventKind.Follow, "", Str(p, "user_id"), Str(p, "username"), Date(p, "created_at"));
    }

    private static ChatterEvent? UserEvent(ReceivedRealtimeEvent row, string slug, ChatterEventKind kind, string suffix,
        string userId, string username, DateTime? at)
        => userId.Length == 0 && username.Length == 0 ? null : Event(row, slug, kind, suffix, userId, username, at);

    private static ChatterEvent Event(ReceivedRealtimeEvent row, string slug, ChatterEventKind kind, string suffix,
        string userId, string username, DateTime? at) => new()
    {
        Id = suffix.Length == 0 ? $"rt:{row.DedupeKey}" : $"rt:{row.DedupeKey}:{suffix}",
        Source = ChatSource.Realtime,
        SourceId = Cap(row.DedupeKey, 200),
        Kind = kind,
        BroadcasterAccountId = null,
        ChannelSlug = Cap(slug, 120),
        UserId = Cap(userId, 64),
        Username = Cap(username, 120),
        OccurredAt = at ?? row.ReceivedAt,
    };
}
