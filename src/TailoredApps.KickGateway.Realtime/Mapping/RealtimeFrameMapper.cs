using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TailoredApps.KickGateway.Contracts.Realtime;
using TailoredApps.KickGateway.Realtime.Pusher;

namespace TailoredApps.KickGateway.Realtime.Mapping;

/// <summary>
/// Pure mapper: a Pusher app-event frame + channel context → a typed realtime contract.
/// All Kick JSON knowledge lives here (keeps the WebSocket client dumb). Unknown/renamed
/// events fall through to <see cref="KickRealtimeUnknown"/> so nothing is dropped. The inner
/// <c>data</c> JSON is preserved on every contract as <c>RawData</c>.
/// </summary>
public class RealtimeFrameMapper
{
    public IKickRealtimeEvent Map(PusherFrame frame, ManagedChannel channel, DateTime receivedAt)
    {
        var raw = frame.Data ?? "";
        JsonElement root;
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            root = doc.RootElement;
        }
        catch
        {
            return Env(new KickRealtimeUnknown(), channel, frame, DedupeKeyFrom(frame, default, raw), receivedAt, null, raw);
        }

        using (doc)
        {
            var dedupe = DedupeKeyFrom(frame, root, raw);
            var ts = Date(root, "created_at");
            var name = ShortName(frame.Event);

            KickRealtimeEventBase evt = name switch
            {
                "ChatMessageEvent" => MapChat(root),
                "MessageDeletedEvent" => MapMessageDeleted(root),
                "UserBannedEvent" => MapBanned(root),
                "UserUnbannedEvent" => MapUnbanned(root),
                "PinnedMessageCreatedEvent" => MapPinnedCreated(root),
                "PinnedMessageDeletedEvent" => new RealtimePinnedMessageDeleted(),
                "PollUpdateEvent" => MapPollUpdate(root),
                "PollDeleteEvent" => new RealtimePollDelete(),
                "ChatroomUpdatedEvent" => MapChatroomUpdated(root),
                "ChatroomClearEvent" => new RealtimeChatroomClear(),
                "SubscriptionEvent" or "ChannelSubscriptionEvent" => MapSubscription(root),
                "GiftedSubscriptionsEvent" => MapGiftedSubs(root),
                "LuckyUsersWhoGotGiftSubscriptionsEvent" => MapLuckyGift(root),
                "StreamerIsLive" => MapStreamerLive(root),
                "StopStreamBroadcast" => MapStreamEnd(root),
                "StreamHostEvent" => MapStreamHost(root),
                "FollowersUpdated" => MapFollowers(root),
                "KicksGifted" => MapKicks(root),
                "RewardRedeemedEvent" => MapReward(root),
                _ => new KickRealtimeUnknown(),
            };

            return Env(evt, channel, frame, dedupe, receivedAt, ts, raw);
        }
    }

    // === per-event mappers ===

    private static RealtimeChatMessage MapChat(JsonElement p)
    {
        var sender = Obj(p, "sender");
        var identity = Obj(sender, "identity");
        var meta = Obj(p, "metadata");
        var origMsg = Obj(meta, "original_message");
        var origSender = Obj(meta, "original_sender");
        return new RealtimeChatMessage
        {
            MessageId = Str(p, "id"),
            Content = Str(p, "content"),
            MessageType = Str(p, "type"),
            CreatedAt = Date(p, "created_at"),
            SenderId = Str(sender, "id"),
            SenderUsername = Str(sender, "username"),
            SenderSlug = Str(sender, "slug"),
            SenderColor = NStr(identity, "color"),
            SenderBadges = Badges(identity),
            ReplyToMessageId = NStr(origMsg, "id"),
            ReplyToSenderUsername = NStr(origSender, "username"),
        };
    }

    private static RealtimeMessageDeleted MapMessageDeleted(JsonElement p) => new()
    {
        MessageId = p.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "id") : Str(p, "id"),
        AiModerated = Bool(p, "aiModerated") || Bool(p, "ai_moderated"),
    };

    private static RealtimeUserBanned MapBanned(JsonElement p)
    {
        var user = Obj(p, "user");
        var by = Obj(p, "banned_by");
        var expires = Date(p, "expires_at");
        var permanent = Bool(p, "permanent") || expires is null && !p.TryGetProperty("expires_at", out _);
        return new RealtimeUserBanned
        {
            BannedUserId = Str(user, "id"),
            BannedUsername = Str(user, "username"),
            BannedSlug = Str(user, "slug"),
            ModeratorUserId = Str(by, "id"),
            ModeratorUsername = Str(by, "username"),
            Permanent = permanent,
            ExpiresAt = expires,
        };
    }

    private static RealtimeUserUnbanned MapUnbanned(JsonElement p)
    {
        var user = Obj(p, "user");
        var by = Obj(p, "unbanned_by");
        return new RealtimeUserUnbanned
        {
            UserId = Str(user, "id"),
            Username = Str(user, "username"),
            Slug = Str(user, "slug"),
            ModeratorUsername = Str(by, "username"),
            Permanent = Bool(p, "permanent"),
        };
    }

    private static RealtimePinnedMessageCreated MapPinnedCreated(JsonElement p)
    {
        var msg = Obj(p, "message");
        var msgSender = Obj(msg, "sender");
        var by = Obj(p, "pinnedBy");
        return new RealtimePinnedMessageCreated
        {
            MessageId = Str(msg, "id"),
            Content = Str(msg, "content"),
            SenderUsername = Str(msgSender, "username"),
            PinnedByUsername = Str(by, "username"),
            DurationSeconds = TryInt(p, "duration"),
        };
    }

    private static RealtimePollUpdate MapPollUpdate(JsonElement p)
    {
        var poll = Obj(p, "poll");
        var options = new List<RealtimePollOption>();
        if (poll.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            foreach (var o in opts.EnumerateArray())
                options.Add(new RealtimePollOption(Int(o, "id"), Str(o, "label"), Int(o, "votes")));
        return new RealtimePollUpdate
        {
            Title = Str(poll, "title"),
            Options = options.ToArray(),
            DurationSeconds = Int(poll, "duration"),
            RemainingSeconds = Int(poll, "remaining"),
        };
    }

    private static RealtimeChatroomUpdated MapChatroomUpdated(JsonElement p)
    {
        var slow = Obj(p, "slow_mode");
        var subs = Obj(p, "subscribers_mode");
        var follow = Obj(p, "followers_mode");
        var emotes = Obj(p, "emotes_mode");
        return new RealtimeChatroomUpdated
        {
            SlowModeEnabled = Bool(slow, "enabled"),
            SlowModeIntervalSeconds = Int(slow, "message_interval"),
            SubscribersOnly = Bool(subs, "enabled"),
            FollowersOnly = Bool(follow, "enabled"),
            FollowersOnlyMinMinutes = Int(follow, "min_duration"),
            EmotesOnly = Bool(emotes, "enabled"),
        };
    }

    private static RealtimeSubscription MapSubscription(JsonElement p) => new()
    {
        Username = Str(p, "username"),
        Months = Int(p, "months"),
    };

    private static RealtimeGiftedSubscriptions MapGiftedSubs(JsonElement p)
    {
        var usernames = StrArray(p, "gifted_usernames");
        return new RealtimeGiftedSubscriptions
        {
            GifterUsername = Str(p, "gifter_username"),
            GiftedUsernames = usernames,
            Count = usernames.Length,
            GifterTotal = TryInt(p, "gifter_total"),
        };
    }

    private static RealtimeLuckyGiftRecipients MapLuckyGift(JsonElement p) => new()
    {
        GifterUsername = Str(p, "gifter_username"),
        Usernames = StrArray(p, "usernames"),
    };

    private static RealtimeStreamerLive MapStreamerLive(JsonElement p)
    {
        var ls = Obj(p, "livestream");
        return new RealtimeStreamerLive
        {
            LivestreamId = Str(ls, "id"),
            Title = NStr(ls, "session_title"),
            StartedAt = Date(ls, "created_at") ?? Date(ls, "start_time"),
        };
    }

    private static RealtimeStreamEnd MapStreamEnd(JsonElement p)
    {
        var ls = Obj(p, "livestream");
        return new RealtimeStreamEnd { LivestreamId = Str(ls, "id") };
    }

    private static RealtimeStreamHost MapStreamHost(JsonElement p) => new()
    {
        HostUsername = Str(p, "host_username"),
        ViewerCount = Int(p, "number_viewers"),
        Message = NStr(p, "optional_message"),
    };

    private static RealtimeFollowersUpdated MapFollowers(JsonElement p) => new()
    {
        FollowersCount = p.TryGetProperty("followersCount", out _) ? Long(p, "followersCount") : Long(p, "followers_count"),
        Username = NStr(p, "username"),
        Followed = p.TryGetProperty("followed", out var f) && (f.ValueKind is JsonValueKind.True or JsonValueKind.False) ? f.GetBoolean() : null,
    };

    private static RealtimeKicksGifted MapKicks(JsonElement p)
    {
        var sender = Obj(p, "sender");
        var gift = Obj(p, "gift");
        return new RealtimeKicksGifted
        {
            SenderUsername = Str(sender, "username"),
            GiftName = Str(gift, "name"),
            Amount = Int(gift, "amount"),
            GiftId = NStr(gift, "gift_id"),
            Message = NStr(p, "message"),
        };
    }

    private static RealtimeRewardRedeemed MapReward(JsonElement p) => new()
    {
        RewardTitle = Str(p, "reward_title"),
        UserId = Str(p, "user_id"),
        Username = Str(p, "username"),
        UserInput = NStr(p, "user_input"),
    };

    // === envelope + dedupe ===

    private static IKickRealtimeEvent Env(KickRealtimeEventBase evt, ManagedChannel c, PusherFrame f,
        string dedupe, DateTime receivedAt, DateTime? ts, string raw)
        => evt with
        {
            BroadcasterSlug = c.Slug,
            KickChannelId = c.ChannelId,
            KickChatroomId = c.ChatroomId,
            PusherChannel = f.Channel ?? "",
            PusherEvent = f.Event,
            DedupeKey = dedupe,
            ReceivedAt = receivedAt,
            KickTimestamp = ts,
            RawData = raw,
        };

    /// <summary>Stable per-frame key: natural <c>id</c> when present, else a short content hash. Public for tests.</summary>
    public static string DedupeKeyFrom(PusherFrame f, JsonElement root, string raw)
    {
        var shortName = ShortName(f.Event);
        string? id = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idEl))
            id = idEl.ValueKind switch
            {
                JsonValueKind.String => idEl.GetString(),
                JsonValueKind.Number => idEl.GetRawText(),
                _ => null,
            };

        var key = !string.IsNullOrEmpty(id)
            ? $"{f.Channel}|{shortName}|{id}"
            : $"{f.Channel}|{shortName}|{Hash(raw)}";
        return key.Length <= 200 ? key : key[..200];
    }

    private static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 12); // 24 hex chars
    }

    /// <summary>The last segment of a Laravel event name — <c>App\Events\ChatMessageEvent</c> → <c>ChatMessageEvent</c>.</summary>
    public static string ShortName(string evt)
    {
        if (string.IsNullOrEmpty(evt)) return "";
        var idx = evt.LastIndexOf('\\');
        return idx >= 0 && idx < evt.Length - 1 ? evt[(idx + 1)..] : evt;
    }

    // === JSON helpers (defensive; Kick payload shapes drift) ===

    private static JsonElement Obj(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string Str(JsonElement el, string prop)
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

    private static string? NStr(JsonElement el, string prop)
    {
        var s = Str(el, prop);
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static int Int(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => 0,
        };
    }

    private static int? TryInt(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static long Long(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String when long.TryParse(v.GetString(), out var s) => s,
            _ => 0,
        };
    }

    private static bool Bool(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b);
    }

    private static DateTime? Date(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var dt)) return dt.ToUniversalTime();
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return null;
    }

    private static string[] StrArray(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
        return list.ToArray();
    }

    private static RealtimeChatBadge[] Badges(JsonElement identity)
    {
        if (identity.ValueKind != JsonValueKind.Object || !identity.TryGetProperty("badges", out var bs) || bs.ValueKind != JsonValueKind.Array)
            return Array.Empty<RealtimeChatBadge>();
        var list = new List<RealtimeChatBadge>();
        foreach (var b in bs.EnumerateArray())
        {
            if (b.ValueKind == JsonValueKind.String) { list.Add(new RealtimeChatBadge(b.GetString() ?? "", "", 0)); continue; }
            if (b.ValueKind == JsonValueKind.Object) list.Add(new RealtimeChatBadge(Str(b, "type"), Str(b, "text"), Int(b, "count")));
        }
        return list.ToArray();
    }
}
