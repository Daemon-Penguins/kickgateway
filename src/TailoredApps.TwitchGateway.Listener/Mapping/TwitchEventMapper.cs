using System.Globalization;
using System.Text.Json;
using TailoredApps.Integrations.Twitch.EventSub;
using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.TwitchGateway.Contracts;
using TailoredApps.TwitchGateway.Contracts.Channel;
using TailoredApps.TwitchGateway.Contracts.Chat;
using TailoredApps.TwitchGateway.Listener.Roster;

namespace TailoredApps.TwitchGateway.Listener.Mapping;

/// <summary>
/// Pure mapper: an EventSub notification (<c>payload.event</c> JSON) or a Helix stream item -> a typed
/// Twitch contract with the envelope filled. All Twitch JSON knowledge lives here; the WebSocket client
/// stays dumb. Unknown or unparseable events become <see cref="TwitchEventUnknown"/> so nothing is lost.
/// </summary>
public static class TwitchEventMapper
{
    public static ITwitchEvent Map(EventSubMessage msg, ManagedTwitchChannel channel, DateTime receivedAt)
    {
        var raw = msg.EventJson ?? "{}";
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(raw);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return Env(new TwitchEventUnknown(), msg, channel, receivedAt, raw, e);

            TwitchEventBase typed = msg.SubscriptionType switch
            {
                EventSubTypes.ChatMessage => MapChatMessage(e),
                EventSubTypes.ChatNotification => MapChatNotification(e),
                EventSubTypes.ChatMessageDelete => new TwitchChatMessageDeleted
                {
                    TargetUserId = Str(e, "target_user_id"),
                    TargetLogin = Str(e, "target_user_login"),
                    TargetName = Str(e, "target_user_name"),
                    ChatMessageId = Str(e, "message_id"),
                },
                EventSubTypes.ChatClear => new TwitchChatCleared(),
                EventSubTypes.ChatClearUserMessages => new TwitchChatUserMessagesCleared
                {
                    TargetUserId = Str(e, "target_user_id"),
                    TargetLogin = Str(e, "target_user_login"),
                    TargetName = Str(e, "target_user_name"),
                },
                EventSubTypes.ChatSettingsUpdate => new TwitchChatSettingsUpdated
                {
                    EmoteMode = Bool(e, "emote_mode"),
                    FollowerMode = Bool(e, "follower_mode"),
                    FollowerModeDurationMinutes = NInt(e, "follower_mode_duration_minutes"),
                    SlowMode = Bool(e, "slow_mode"),
                    SlowModeWaitTimeSeconds = NInt(e, "slow_mode_wait_time_seconds"),
                    SubscriberMode = Bool(e, "subscriber_mode"),
                    UniqueChatMode = Bool(e, "unique_chat_mode"),
                },
                _ => new TwitchEventUnknown(),
            };
            return Env(typed, msg, channel, receivedAt, raw, e);
        }
        catch (JsonException)
        {
            return Env(new TwitchEventUnknown(), msg, channel, receivedAt, raw, default);
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private static ITwitchEvent Env(TwitchEventBase typed, EventSubMessage msg, ManagedTwitchChannel channel, DateTime receivedAt, string raw, JsonElement e)
    {
        var name = e.ValueKind == JsonValueKind.Object ? Str(e, "broadcaster_user_name") : "";
        return typed with
        {
            BroadcasterLogin = channel.Login,
            BroadcasterUserId = channel.UserId,
            BroadcasterName = name.Length > 0 ? name : channel.DisplayName,
            EventType = msg.SubscriptionType ?? "",
            EventVersion = msg.SubscriptionVersion ?? "",
            MessageId = msg.MessageId,
            DedupeKey = DedupeKeyFor(msg),
            ReceivedAt = receivedAt,
            TwitchTimestamp = msg.Timestamp,
            RawPayload = raw,
        };
    }

    /// <summary><c>es|&lt;message_id&gt;</c>: EventSub message ids are unique per notification, redeliveries reuse them.</summary>
    public static string DedupeKeyFor(EventSubMessage msg)
    {
        var key = "es|" + msg.MessageId;
        return key.Length <= 200 ? key : key[..200];
    }

    // ---- Helix polling (synthetic events) ----

    public static TwitchStreamOnline StreamOnline(TwitchStream s, ManagedTwitchChannel channel, DateTime receivedAt, string rawJson) => new()
    {
        BroadcasterLogin = channel.Login,
        BroadcasterUserId = channel.UserId,
        BroadcasterName = s.UserName.Length > 0 ? s.UserName : channel.DisplayName,
        EventType = "helix.stream.online",
        MessageId = $"helix-online-{s.Id}",
        DedupeKey = $"helix|online|{s.Id}",
        ReceivedAt = receivedAt,
        TwitchTimestamp = s.StartedAt,
        RawPayload = rawJson,
        StreamId = s.Id,
        StreamType = s.Type,
        StartedAt = s.StartedAt,
        Title = s.Title,
        CategoryId = s.GameId,
        CategoryName = s.GameName,
        Language = s.Language,
        ViewerCount = s.ViewerCount,
    };

    public static TwitchChannelUpdated ChannelUpdated(TwitchStream s, ManagedTwitchChannel channel, DateTime receivedAt, string rawJson)
    {
        var fingerprint = Fingerprint(s.Title, s.GameId, s.Language);
        return new TwitchChannelUpdated
        {
            BroadcasterLogin = channel.Login,
            BroadcasterUserId = channel.UserId,
            BroadcasterName = s.UserName.Length > 0 ? s.UserName : channel.DisplayName,
            EventType = "helix.channel.update",
            MessageId = $"helix-update-{s.Id}-{fingerprint}",
            DedupeKey = $"helix|update|{s.Id}|{fingerprint}",
            ReceivedAt = receivedAt,
            TwitchTimestamp = receivedAt,
            RawPayload = rawJson,
            StreamId = s.Id,
            Title = s.Title,
            CategoryId = s.GameId,
            CategoryName = s.GameName,
            Language = s.Language,
            ViewerCount = s.ViewerCount,
        };
    }

    public static TwitchStreamOffline StreamOffline(string streamId, DateTime? startedAt, ManagedTwitchChannel channel, DateTime receivedAt) => new()
    {
        BroadcasterLogin = channel.Login,
        BroadcasterUserId = channel.UserId,
        BroadcasterName = channel.DisplayName,
        EventType = "helix.stream.offline",
        MessageId = $"helix-offline-{streamId}",
        DedupeKey = $"helix|offline|{streamId}",
        ReceivedAt = receivedAt,
        TwitchTimestamp = receivedAt,
        RawPayload = JsonSerializer.Serialize(new { stream_id = streamId, started_at = startedAt, ended_at = receivedAt }),
        StreamId = streamId,
        StartedAt = startedAt,
        EndedAt = receivedAt,
    };

    /// <summary>Short stable hash of the mutable stream fields (title/category/language) - part of the update dedupe key.</summary>
    public static string Fingerprint(string title, string categoryId, string language)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{title}\n{categoryId}\n{language}"));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 8));
    }

    // ---- chat ----

    private static TwitchChatMessage MapChatMessage(JsonElement e)
    {
        var message = Obj(e, "message");
        var cheer = Obj(e, "cheer");
        var reply = Obj(e, "reply");
        return new TwitchChatMessage
        {
            ChatMessageId = Str(e, "message_id"),
            Text = Str(message, "text"),
            MessageType = Str(e, "message_type") is { Length: > 0 } mt ? mt : "text",
            ChatterUserId = Str(e, "chatter_user_id"),
            ChatterLogin = Str(e, "chatter_user_login"),
            ChatterName = Str(e, "chatter_user_name"),
            Color = Str(e, "color"),
            Badges = Badges(e, "badges"),
            Fragments = Fragments(message),
            CheerBits = cheer.ValueKind == JsonValueKind.Object ? Int(cheer, "bits") : null,
            Reply = reply.ValueKind == JsonValueKind.Object
                ? new TwitchChatReply
                {
                    ParentMessageId = Str(reply, "parent_message_id"),
                    ParentMessageBody = Str(reply, "parent_message_body"),
                    ParentUserId = Str(reply, "parent_user_id"),
                    ParentLogin = Str(reply, "parent_user_login"),
                    ParentName = Str(reply, "parent_user_name"),
                    ThreadMessageId = Str(reply, "thread_message_id"),
                    ThreadUserId = Str(reply, "thread_user_id"),
                    ThreadLogin = Str(reply, "thread_user_login"),
                    ThreadName = Str(reply, "thread_user_name"),
                }
                : null,
            ChannelPointsCustomRewardId = NStr(e, "channel_points_custom_reward_id"),
            SourceBroadcasterUserId = NStr(e, "source_broadcaster_user_id"),
            SourceBroadcasterLogin = NStr(e, "source_broadcaster_user_login"),
            SourceMessageId = NStr(e, "source_message_id"),
            IsSourceOnly = NBool(e, "is_source_only"),
        };
    }

    private static TwitchChatNotification MapChatNotification(JsonElement e)
    {
        var message = Obj(e, "message");
        var notice = Str(e, "notice_type");
        // shared_chat_* notices carry the same objects under a prefixed name - fold them onto the plain properties.
        var shared = notice.StartsWith("shared_chat_", StringComparison.Ordinal);
        JsonElement Part(string name) => Obj(e, shared ? "shared_chat_" + name : name) is { ValueKind: JsonValueKind.Object } p ? p : Obj(e, name);

        var sub = Part("sub");
        var resub = Part("resub");
        var gift = Part("sub_gift");
        var community = Part("community_sub_gift");
        var paidUpgrade = Part("gift_paid_upgrade");
        var prime = Part("prime_paid_upgrade");
        var pif = Part("pay_it_forward");
        var raid = Part("raid");
        var ann = Part("announcement");
        var bits = Obj(e, "bits_badge_tier");
        var charity = Obj(e, "charity_donation");
        var streak = Obj(e, "watch_streak");
        var amount = Obj(charity, "amount");

        return new TwitchChatNotification
        {
            ChatMessageId = Str(e, "message_id"),
            NoticeType = notice,
            SystemMessage = Str(e, "system_message"),
            ChatterUserId = Str(e, "chatter_user_id"),
            ChatterLogin = Str(e, "chatter_user_login"),
            ChatterName = Str(e, "chatter_user_name"),
            ChatterIsAnonymous = Bool(e, "chatter_is_anonymous"),
            Color = Str(e, "color"),
            Badges = Badges(e, "badges"),
            Text = Str(message, "text"),
            Fragments = Fragments(message),
            Sub = sub.ValueKind == JsonValueKind.Object ? new TwitchSubNotice(Str(sub, "sub_tier"), Bool(sub, "is_prime"), Int(sub, "duration_months")) : null,
            Resub = resub.ValueKind == JsonValueKind.Object
                ? new TwitchResubNotice(Int(resub, "cumulative_months"), Int(resub, "streak_months"), Int(resub, "duration_months"), Str(resub, "sub_tier"),
                    Bool(resub, "is_prime"), Bool(resub, "is_gift"), Bool(resub, "gifter_is_anonymous"), NStr(resub, "gifter_user_id"), NStr(resub, "gifter_user_login"), NStr(resub, "gifter_user_name"))
                : null,
            SubGift = gift.ValueKind == JsonValueKind.Object
                ? new TwitchSubGiftNotice(Int(gift, "duration_months"), NInt(gift, "cumulative_total"), Str(gift, "recipient_user_id"), Str(gift, "recipient_user_login"),
                    Str(gift, "recipient_user_name"), Str(gift, "sub_tier"), NStr(gift, "community_gift_id"))
                : null,
            CommunitySubGift = community.ValueKind == JsonValueKind.Object
                ? new TwitchCommunitySubGiftNotice(Str(community, "id"), Int(community, "total"), Str(community, "sub_tier"), NInt(community, "cumulative_total"))
                : null,
            GiftPaidUpgrade = paidUpgrade.ValueKind == JsonValueKind.Object
                ? new TwitchGiftPaidUpgradeNotice(Bool(paidUpgrade, "gifter_is_anonymous"), NStr(paidUpgrade, "gifter_user_id"), NStr(paidUpgrade, "gifter_user_name"))
                : null,
            PrimePaidUpgrade = prime.ValueKind == JsonValueKind.Object ? new TwitchPrimePaidUpgradeNotice(Str(prime, "sub_tier")) : null,
            PayItForward = pif.ValueKind == JsonValueKind.Object
                ? new TwitchPayItForwardNotice(Bool(pif, "gifter_is_anonymous"), NStr(pif, "gifter_user_id"), NStr(pif, "gifter_user_login"), NStr(pif, "gifter_user_name"))
                : null,
            Raid = raid.ValueKind == JsonValueKind.Object
                ? new TwitchRaidNotice(Str(raid, "user_id"), Str(raid, "user_login"), Str(raid, "user_name"), Int(raid, "viewer_count"), Str(raid, "profile_image_url"))
                : null,
            Announcement = ann.ValueKind == JsonValueKind.Object ? new TwitchAnnouncementNotice(Str(ann, "color")) : null,
            BitsBadgeTier = bits.ValueKind == JsonValueKind.Object ? new TwitchBitsBadgeTierNotice(Int(bits, "tier")) : null,
            CharityDonation = charity.ValueKind == JsonValueKind.Object
                ? new TwitchCharityDonationNotice(Str(charity, "charity_name"), Money(amount), Str(amount, "currency"))
                : null,
            WatchStreak = streak.ValueKind == JsonValueKind.Object ? new TwitchWatchStreakNotice(Int(streak, "streak_count"), Int(streak, "channel_points_awarded")) : null,
            SourceBroadcasterUserId = NStr(e, "source_broadcaster_user_id"),
            SourceBroadcasterLogin = NStr(e, "source_broadcaster_user_login"),
            SourceMessageId = NStr(e, "source_message_id"),
            IsSourceOnly = NBool(e, "is_source_only"),
        };
    }

    private static TwitchChatBadge[] Badges(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<TwitchChatBadge>();
        foreach (var b in arr.EnumerateArray())
            if (b.ValueKind == JsonValueKind.Object) list.Add(new TwitchChatBadge(Str(b, "set_id"), Str(b, "id"), Str(b, "info")));
        return list.ToArray();
    }

    private static TwitchChatFragment[] Fragments(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("fragments", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<TwitchChatFragment>();
        foreach (var f in arr.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            var emote = Obj(f, "emote");
            var cheermote = Obj(f, "cheermote");
            var mention = Obj(f, "mention");
            list.Add(new TwitchChatFragment
            {
                Type = Str(f, "type") is { Length: > 0 } t ? t : "text",
                Text = Str(f, "text"),
                EmoteId = emote.ValueKind == JsonValueKind.Object ? NStr(emote, "id") : null,
                EmoteSetId = emote.ValueKind == JsonValueKind.Object ? NStr(emote, "emote_set_id") : null,
                EmoteOwnerId = emote.ValueKind == JsonValueKind.Object ? NStr(emote, "owner_id") : null,
                CheermotePrefix = cheermote.ValueKind == JsonValueKind.Object ? NStr(cheermote, "prefix") : null,
                CheermoteBits = cheermote.ValueKind == JsonValueKind.Object ? Int(cheermote, "bits") : null,
                CheermoteTier = cheermote.ValueKind == JsonValueKind.Object ? Int(cheermote, "tier") : null,
                MentionUserId = mention.ValueKind == JsonValueKind.Object ? NStr(mention, "user_id") : null,
                MentionLogin = mention.ValueKind == JsonValueKind.Object ? NStr(mention, "user_login") : null,
                MentionName = mention.ValueKind == JsonValueKind.Object ? NStr(mention, "user_name") : null,
            });
        }
        return list.ToArray();
    }

    private static decimal Money(JsonElement amount)
    {
        if (amount.ValueKind != JsonValueKind.Object) return 0;
        var value = Int(amount, "value");
        var places = Int(amount, "decimal_place");
        return places <= 0 ? value : value / (decimal)Math.Pow(10, places);
    }

    // ---- JSON helpers ----

    internal static JsonElement Obj(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    internal static string Str(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        };
    }

    internal static string? NStr(JsonElement el, string prop) => Str(el, prop) is { Length: > 0 } s ? s : null;

    internal static int Int(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => 0,
        };
    }

    internal static int? NInt(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    internal static bool Bool(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    internal static bool? NBool(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;
}
