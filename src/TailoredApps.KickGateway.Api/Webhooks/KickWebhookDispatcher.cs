using System.Text.Json;
using MassTransit;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Events;

namespace TailoredApps.KickGateway.Api.Webhooks;

/// <summary>
/// Translates a verified Kick webhook payload (raw JSON + headers) into the
/// matching MassTransit contract and publishes via <see cref="IPublishEndpoint"/>.
/// The publish lands in the EF outbox because <see cref="KickGatewayDbContext"/>
/// owns the transaction — MassTransit dispatches to RabbitMQ after the DB commit.
/// </summary>
public class KickWebhookDispatcher
{
    private readonly IPublishEndpoint _publisher;
    private readonly ILogger<KickWebhookDispatcher> _log;

    public KickWebhookDispatcher(IPublishEndpoint publisher, ILogger<KickWebhookDispatcher> log)
    {
        _publisher = publisher;
        _log = log;
    }

    public async Task PublishAsync(
        string eventType,
        string eventVersion,
        string messageId,
        string subscriptionId,
        DateTime? kickTimestamp,
        DateTime receivedAt,
        KickBroadcasterAccount broadcaster,
        string rawBody,
        CancellationToken ct)
    {
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            payload = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Webhook body was not valid JSON; publishing as Unknown ({Type})", eventType);
            await PublishUnknown(eventType, eventVersion, messageId, subscriptionId, kickTimestamp, receivedAt, broadcaster, rawBody, ct);
            return;
        }

        // common envelope fields
        var commonInit = new EnvelopeFields(
            KickMessageId: messageId,
            KickSubscriptionId: subscriptionId,
            BroadcasterAccountId: broadcaster.Id,
            BroadcasterUserId: broadcaster.KickUserId,
            BroadcasterSlug: broadcaster.ChannelSlug,
            ReceivedAt: receivedAt,
            KickTimestamp: kickTimestamp,
            RawPayload: rawBody);

        switch (eventType)
        {
            case KickEventTypes.ChatMessageSent:
                await _publisher.Publish(MapChat(payload, commonInit), ct);
                break;
            case KickEventTypes.ChannelFollowed:
                await _publisher.Publish(MapFollowed(payload, commonInit), ct);
                break;
            case KickEventTypes.SubscriptionNew:
                await _publisher.Publish(MapSubNew(payload, commonInit), ct);
                break;
            case KickEventTypes.SubscriptionGifts:
                await _publisher.Publish(MapSubGifts(payload, commonInit), ct);
                break;
            case KickEventTypes.SubscriptionRenewal:
                await _publisher.Publish(MapSubRenewal(payload, commonInit), ct);
                break;
            case KickEventTypes.LivestreamStatusUpdated:
                await _publisher.Publish(MapLivestreamStatus(payload, commonInit), ct);
                break;
            case KickEventTypes.LivestreamMetadataUpdated:
                await _publisher.Publish(MapLivestreamMetadata(payload, commonInit), ct);
                break;
            case KickEventTypes.ModerationBanned:
                await _publisher.Publish(MapModerationBanned(payload, commonInit), ct);
                break;
            case KickEventTypes.KicksGifted:
                await _publisher.Publish(MapKicksGifted(payload, commonInit), ct);
                break;
            case KickEventTypes.ChannelRewardRedemptionUpdated:
                await _publisher.Publish(MapRewardRedemption(payload, commonInit), ct);
                break;
            default:
                _log.LogWarning("Unmapped Kick event type {Type} — publishing as Unknown", eventType);
                await PublishUnknown(eventType, eventVersion, messageId, subscriptionId, kickTimestamp, receivedAt, broadcaster, rawBody, ct);
                break;
        }
    }

    private Task PublishUnknown(string eventType, string eventVersion, string messageId, string subscriptionId,
        DateTime? kickTimestamp, DateTime receivedAt, KickBroadcasterAccount broadcaster, string rawBody, CancellationToken ct)
    {
        var msg = new KickEventUnknown
        {
            EventType = eventType,
            EventVersion = eventVersion,
            KickMessageId = messageId,
            KickSubscriptionId = subscriptionId,
            BroadcasterAccountId = broadcaster.Id,
            BroadcasterUserId = broadcaster.KickUserId,
            BroadcasterSlug = broadcaster.ChannelSlug,
            ReceivedAt = receivedAt,
            KickTimestamp = kickTimestamp,
            RawPayload = rawBody
        };
        return _publisher.Publish(msg, ct);
    }

    // === mappers ===

    private record EnvelopeFields(string KickMessageId, string KickSubscriptionId, Guid BroadcasterAccountId,
        string BroadcasterUserId, string BroadcasterSlug, DateTime ReceivedAt, DateTime? KickTimestamp, string RawPayload);

    private static void Fill(KickEventBase target, EnvelopeFields e)
    {
        // helper would be cleaner if records were mutable; just write each subclass init in-line below.
    }

    private static ChatMessageSent MapChat(JsonElement p, EnvelopeFields e)
    {
        var sender = p.TryGetProperty("sender", out var s) ? s : default;
        return new ChatMessageSent
        {
            KickMessageId = e.KickMessageId,
            KickSubscriptionId = e.KickSubscriptionId,
            BroadcasterAccountId = e.BroadcasterAccountId,
            BroadcasterUserId = e.BroadcasterUserId,
            BroadcasterSlug = e.BroadcasterSlug,
            ReceivedAt = e.ReceivedAt,
            KickTimestamp = e.KickTimestamp,
            RawPayload = e.RawPayload,
            MessageId = ReadString(p, "message_id"),
            Content = ReadString(p, "content"),
            CreatedAt = ReadDate(p, "created_at") ?? e.ReceivedAt,
            SenderUserId = sender.ValueKind == JsonValueKind.Object ? ReadString(sender, "user_id") : "",
            SenderUsername = sender.ValueKind == JsonValueKind.Object ? ReadString(sender, "username") : "",
            SenderIdentityColor = sender.ValueKind == JsonValueKind.Object && sender.TryGetProperty("identity", out var ident)
                && ident.TryGetProperty("username_color", out var col) && col.ValueKind == JsonValueKind.String ? col.GetString() : null,
            SenderBadges = sender.ValueKind == JsonValueKind.Object && sender.TryGetProperty("identity", out var ident2)
                && ident2.TryGetProperty("badges", out var bs) && bs.ValueKind == JsonValueKind.Array
                    ? bs.EnumerateArray().Select(b => b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : b.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "").ToArray()
                    : Array.Empty<string>()
        };
    }

    private static ChannelFollowed MapFollowed(JsonElement p, EnvelopeFields e)
    {
        var f = p.TryGetProperty("follower", out var fo) ? fo : default;
        return new ChannelFollowed
        {
            KickMessageId = e.KickMessageId,
            KickSubscriptionId = e.KickSubscriptionId,
            BroadcasterAccountId = e.BroadcasterAccountId,
            BroadcasterUserId = e.BroadcasterUserId,
            BroadcasterSlug = e.BroadcasterSlug,
            ReceivedAt = e.ReceivedAt,
            KickTimestamp = e.KickTimestamp,
            RawPayload = e.RawPayload,
            FollowerUserId = f.ValueKind == JsonValueKind.Object ? ReadString(f, "user_id") : "",
            FollowerUsername = f.ValueKind == JsonValueKind.Object ? ReadString(f, "username") : ""
        };
    }

    private static ChannelSubscriptionNew MapSubNew(JsonElement p, EnvelopeFields e)
    {
        var sub = p.TryGetProperty("subscriber", out var s) ? s : default;
        return new ChannelSubscriptionNew
        {
            KickMessageId = e.KickMessageId,
            KickSubscriptionId = e.KickSubscriptionId,
            BroadcasterAccountId = e.BroadcasterAccountId,
            BroadcasterUserId = e.BroadcasterUserId,
            BroadcasterSlug = e.BroadcasterSlug,
            ReceivedAt = e.ReceivedAt,
            KickTimestamp = e.KickTimestamp,
            RawPayload = e.RawPayload,
            SubscriberUserId = sub.ValueKind == JsonValueKind.Object ? ReadString(sub, "user_id") : "",
            SubscriberUsername = sub.ValueKind == JsonValueKind.Object ? ReadString(sub, "username") : "",
            Duration = ReadInt(p, "duration"),
            CreatedAt = ReadDate(p, "created_at")
        };
    }

    private static ChannelSubscriptionGifts MapSubGifts(JsonElement p, EnvelopeFields e)
    {
        var gifter = p.TryGetProperty("gifter", out var g) ? g : default;
        return new ChannelSubscriptionGifts
        {
            KickMessageId = e.KickMessageId,
            KickSubscriptionId = e.KickSubscriptionId,
            BroadcasterAccountId = e.BroadcasterAccountId,
            BroadcasterUserId = e.BroadcasterUserId,
            BroadcasterSlug = e.BroadcasterSlug,
            ReceivedAt = e.ReceivedAt,
            KickTimestamp = e.KickTimestamp,
            RawPayload = e.RawPayload,
            GifterUserId = gifter.ValueKind == JsonValueKind.Object ? ReadString(gifter, "user_id") : "",
            GifterUsername = gifter.ValueKind == JsonValueKind.Object ? ReadString(gifter, "username") : "",
            GiftCount = ReadInt(p, "gift_count"),
            Tier = ReadInt(p, "tier"),
            RecipientUserIds = p.TryGetProperty("recipients", out var rs) && rs.ValueKind == JsonValueKind.Array
                ? rs.EnumerateArray().Select(r => ReadString(r, "user_id")).ToArray() : Array.Empty<string>(),
            RecipientUsernames = p.TryGetProperty("recipients", out var rs2) && rs2.ValueKind == JsonValueKind.Array
                ? rs2.EnumerateArray().Select(r => ReadString(r, "username")).ToArray() : Array.Empty<string>(),
            CreatedAt = ReadDate(p, "created_at")
        };
    }

    private static ChannelSubscriptionRenewal MapSubRenewal(JsonElement p, EnvelopeFields e)
    {
        var sub = p.TryGetProperty("subscriber", out var s) ? s : default;
        return new ChannelSubscriptionRenewal
        {
            KickMessageId = e.KickMessageId,
            KickSubscriptionId = e.KickSubscriptionId,
            BroadcasterAccountId = e.BroadcasterAccountId,
            BroadcasterUserId = e.BroadcasterUserId,
            BroadcasterSlug = e.BroadcasterSlug,
            ReceivedAt = e.ReceivedAt,
            KickTimestamp = e.KickTimestamp,
            RawPayload = e.RawPayload,
            SubscriberUserId = sub.ValueKind == JsonValueKind.Object ? ReadString(sub, "user_id") : "",
            SubscriberUsername = sub.ValueKind == JsonValueKind.Object ? ReadString(sub, "username") : "",
            Duration = ReadInt(p, "duration"),
            CumulativeMonths = ReadInt(p, "cumulative_months"),
            CreatedAt = ReadDate(p, "created_at")
        };
    }

    private static LivestreamStatusUpdated MapLivestreamStatus(JsonElement p, EnvelopeFields e) => new()
    {
        KickMessageId = e.KickMessageId,
        KickSubscriptionId = e.KickSubscriptionId,
        BroadcasterAccountId = e.BroadcasterAccountId,
        BroadcasterUserId = e.BroadcasterUserId,
        BroadcasterSlug = e.BroadcasterSlug,
        ReceivedAt = e.ReceivedAt,
        KickTimestamp = e.KickTimestamp,
        RawPayload = e.RawPayload,
        IsLive = ReadBool(p, "is_live"),
        Title = p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
        StartedAt = ReadDate(p, "started_at"),
        EndedAt = ReadDate(p, "ended_at")
    };

    private static LivestreamMetadataUpdated MapLivestreamMetadata(JsonElement p, EnvelopeFields e)
    {
        var cat = p.TryGetProperty("category", out var c) ? c : default;
        return new LivestreamMetadataUpdated
        {
            KickMessageId = e.KickMessageId,
            KickSubscriptionId = e.KickSubscriptionId,
            BroadcasterAccountId = e.BroadcasterAccountId,
            BroadcasterUserId = e.BroadcasterUserId,
            BroadcasterSlug = e.BroadcasterSlug,
            ReceivedAt = e.ReceivedAt,
            KickTimestamp = e.KickTimestamp,
            RawPayload = e.RawPayload,
            Title = p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
            Language = p.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null,
            HasMatureContent = p.TryGetProperty("has_mature_content", out var m) && (m.ValueKind == JsonValueKind.True || m.ValueKind == JsonValueKind.False) ? m.GetBoolean() : null,
            CategoryId = cat.ValueKind == JsonValueKind.Object ? ReadString(cat, "id") : null,
            CategoryName = cat.ValueKind == JsonValueKind.Object ? ReadString(cat, "name") : null
        };
    }

    private static ModerationBanned MapModerationBanned(JsonElement p, EnvelopeFields e)
    {
        var banned = p.TryGetProperty("banned_user", out var b) ? b : default;
        var mod = p.TryGetProperty("moderator", out var m) ? m : default;
        var meta = p.TryGetProperty("metadata", out var md) ? md : default;
        var expires = meta.ValueKind == JsonValueKind.Object ? ReadDate(meta, "expires_at") : null;
        return new ModerationBanned
        {
            KickMessageId = e.KickMessageId,
            KickSubscriptionId = e.KickSubscriptionId,
            BroadcasterAccountId = e.BroadcasterAccountId,
            BroadcasterUserId = e.BroadcasterUserId,
            BroadcasterSlug = e.BroadcasterSlug,
            ReceivedAt = e.ReceivedAt,
            KickTimestamp = e.KickTimestamp,
            RawPayload = e.RawPayload,
            BannedUserId = banned.ValueKind == JsonValueKind.Object ? ReadString(banned, "user_id") : "",
            BannedUsername = banned.ValueKind == JsonValueKind.Object ? ReadString(banned, "username") : "",
            ModeratorUserId = mod.ValueKind == JsonValueKind.Object ? ReadString(mod, "user_id") : "",
            ModeratorUsername = mod.ValueKind == JsonValueKind.Object ? ReadString(mod, "username") : "",
            Reason = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            BannedAt = ReadDate(p, "banned_at"),
            ExpiresAt = expires,
            IsPermanent = expires is null
        };
    }

    private static KicksGifted MapKicksGifted(JsonElement p, EnvelopeFields e)
    {
        var gifter = p.TryGetProperty("gifter", out var g) ? g : default;
        return new KicksGifted
        {
            KickMessageId = e.KickMessageId,
            KickSubscriptionId = e.KickSubscriptionId,
            BroadcasterAccountId = e.BroadcasterAccountId,
            BroadcasterUserId = e.BroadcasterUserId,
            BroadcasterSlug = e.BroadcasterSlug,
            ReceivedAt = e.ReceivedAt,
            KickTimestamp = e.KickTimestamp,
            RawPayload = e.RawPayload,
            GifterUserId = gifter.ValueKind == JsonValueKind.Object ? ReadString(gifter, "user_id") : "",
            GifterUsername = gifter.ValueKind == JsonValueKind.Object ? ReadString(gifter, "username") : "",
            Amount = ReadInt(p, "amount"),
            Message = p.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
            CreatedAt = ReadDate(p, "created_at")
        };
    }

    private static ChannelRewardRedemptionUpdated MapRewardRedemption(JsonElement p, EnvelopeFields e)
    {
        var user = p.TryGetProperty("user", out var u) ? u : default;
        var reward = p.TryGetProperty("reward", out var r) ? r : default;
        return new ChannelRewardRedemptionUpdated
        {
            KickMessageId = e.KickMessageId,
            KickSubscriptionId = e.KickSubscriptionId,
            BroadcasterAccountId = e.BroadcasterAccountId,
            BroadcasterUserId = e.BroadcasterUserId,
            BroadcasterSlug = e.BroadcasterSlug,
            ReceivedAt = e.ReceivedAt,
            KickTimestamp = e.KickTimestamp,
            RawPayload = e.RawPayload,
            RedemptionId = ReadString(p, "redemption_id"),
            RewardId = reward.ValueKind == JsonValueKind.Object ? ReadString(reward, "id") : "",
            RewardTitle = reward.ValueKind == JsonValueKind.Object ? ReadString(reward, "title") : "",
            UserId = user.ValueKind == JsonValueKind.Object ? ReadString(user, "user_id") : "",
            Username = user.ValueKind == JsonValueKind.Object ? ReadString(user, "username") : "",
            Status = ReadString(p, "status"),
            UserInput = p.TryGetProperty("user_input", out var ui) && ui.ValueKind == JsonValueKind.String ? ui.GetString() : null,
            RedeemedAt = ReadDate(p, "redeemed_at")
        };
    }

    private static string ReadString(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => ""
        };
    }

    private static int ReadInt(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => 0
        };
    }

    private static bool ReadBool(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
           && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    private static DateTime? ReadDate(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var dt)) return dt.ToUniversalTime();
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return null;
    }
}
