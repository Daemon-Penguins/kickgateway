namespace TailoredApps.Integrations.Kick;

/// <summary>
/// Catalogue of Kick event types per https://docs.kick.com/events/event-types
/// (verified 2026-05-13). Unknown names cause 400 "Invalid request" from the
/// subscriptions endpoint.
/// </summary>
public static class KickEventTypes
{
    public const string ChatMessageSent = "chat.message.sent";
    public const string ChannelFollowed = "channel.followed";
    public const string SubscriptionNew = "channel.subscription.new";
    public const string SubscriptionGifts = "channel.subscription.gifts";
    public const string SubscriptionRenewal = "channel.subscription.renewal";
    public const string LivestreamStatusUpdated = "livestream.status.updated";
    public const string LivestreamMetadataUpdated = "livestream.metadata.updated";
    public const string ModerationBanned = "moderation.banned";
    public const string KicksGifted = "kicks.gifted";
    public const string ChannelRewardRedemptionUpdated = "channel.reward.redemption.updated";

    /// <summary>The full subscribable set the gateway will enroll a broadcaster in by default.</summary>
    public static readonly IReadOnlyList<(string Event, int Version)> All = new[]
    {
        (ChatMessageSent, 1),
        (ChannelFollowed, 1),
        (SubscriptionNew, 1),
        (SubscriptionGifts, 1),
        (SubscriptionRenewal, 1),
        (LivestreamStatusUpdated, 1),
        (LivestreamMetadataUpdated, 1),
        (ModerationBanned, 1),
        (KicksGifted, 1),
        (ChannelRewardRedemptionUpdated, 1)
    };

    public const string HeaderEventType = "Kick-Event-Type";
    public const string HeaderEventVersion = "Kick-Event-Version";
    public const string HeaderMessageId = "Kick-Event-Message-Id";
    public const string HeaderSubscriptionId = "Kick-Event-Subscription-Id";
    public const string HeaderSignature = "Kick-Event-Signature";
    public const string HeaderMessageTimestamp = "Kick-Event-Message-Timestamp";
}
