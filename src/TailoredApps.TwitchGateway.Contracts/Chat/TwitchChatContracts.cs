namespace TailoredApps.TwitchGateway.Contracts.Chat;

/// <summary>A chat badge as EventSub reports it (<c>set_id</c> = <c>subscriber</c>, <c>moderator</c>, <c>vip</c>, ...; <c>id</c> = version, e.g. months tier; <c>info</c> = extra, e.g. exact sub months).</summary>
public record TwitchChatBadge(string SetId, string Id, string Info);

/// <summary>One fragment of a chat message: <c>text</c>, <c>emote</c>, <c>cheermote</c>, <c>mention</c> or <c>gif</c>.</summary>
public record TwitchChatFragment
{
    public string Type { get; init; } = "text";
    public string Text { get; init; } = "";
    public string? EmoteId { get; init; }
    public string? EmoteSetId { get; init; }
    public string? EmoteOwnerId { get; init; }
    public string? CheermotePrefix { get; init; }
    public int? CheermoteBits { get; init; }
    public int? CheermoteTier { get; init; }
    public string? MentionUserId { get; init; }
    public string? MentionLogin { get; init; }
    public string? MentionName { get; init; }
}

/// <summary>Reply metadata when the message used Twitch's reply feature.</summary>
public record TwitchChatReply
{
    public string ParentMessageId { get; init; } = "";
    public string ParentMessageBody { get; init; } = "";
    public string ParentUserId { get; init; } = "";
    public string ParentLogin { get; init; } = "";
    public string ParentName { get; init; } = "";
    public string ThreadMessageId { get; init; } = "";
    public string ThreadUserId { get; init; } = "";
    public string ThreadLogin { get; init; } = "";
    public string ThreadName { get; init; } = "";
}

/// <summary>
/// EventSub <c>channel.chat.message</c> (v1): one chat message in the broadcaster's channel.
/// <see cref="Text"/> is the plain text; emotes/cheermotes/mentions are in <see cref="Fragments"/>.
/// </summary>
public record TwitchChatMessage : TwitchEventBase
{
    /// <summary>Twitch's chat message id (UUID).</summary>
    public string ChatMessageId { get; init; } = "";

    public string Text { get; init; } = "";

    /// <summary><c>text</c>, <c>channel_points_highlighted</c>, <c>channel_points_sub_only</c>, <c>user_intro</c>, <c>power_ups_message_effect</c>, <c>power_ups_gigantified_emote</c>.</summary>
    public string MessageType { get; init; } = "text";

    public string ChatterUserId { get; init; } = "";
    public string ChatterLogin { get; init; } = "";
    public string ChatterName { get; init; } = "";

    /// <summary>Hex RGB (<c>#1E90FF</c>), empty when the user never picked one.</summary>
    public string Color { get; init; } = "";

    public TwitchChatBadge[] Badges { get; init; } = [];
    public TwitchChatFragment[] Fragments { get; init; } = [];

    /// <summary>Bits cheered with this message, null when none.</summary>
    public int? CheerBits { get; init; }

    public TwitchChatReply? Reply { get; init; }

    /// <summary>Set when the message redeemed a custom channel-points reward.</summary>
    public string? ChannelPointsCustomRewardId { get; init; }

    /// <summary>Shared chat: the channel the message was originally sent in (null outside shared chat).</summary>
    public string? SourceBroadcasterUserId { get; init; }
    public string? SourceBroadcasterLogin { get; init; }
    public string? SourceMessageId { get; init; }
    public bool? IsSourceOnly { get; init; }
}

public record TwitchSubNotice(string Tier, bool IsPrime, int DurationMonths);

public record TwitchResubNotice(int CumulativeMonths, int StreakMonths, int DurationMonths, string Tier, bool IsPrime, bool IsGift,
    bool GifterIsAnonymous, string? GifterUserId, string? GifterLogin, string? GifterName);

public record TwitchSubGiftNotice(int DurationMonths, int? CumulativeTotal, string RecipientUserId, string RecipientLogin, string RecipientName,
    string Tier, string? CommunityGiftId);

public record TwitchCommunitySubGiftNotice(string Id, int Total, string Tier, int? CumulativeTotal);

public record TwitchRaidNotice(string UserId, string Login, string Name, int ViewerCount, string ProfileImageUrl);

public record TwitchAnnouncementNotice(string Color);

public record TwitchBitsBadgeTierNotice(int Tier);

public record TwitchCharityDonationNotice(string CharityName, decimal Amount, string Currency);

public record TwitchPayItForwardNotice(bool GifterIsAnonymous, string? GifterUserId, string? GifterLogin, string? GifterName);

public record TwitchGiftPaidUpgradeNotice(bool GifterIsAnonymous, string? GifterUserId, string? GifterName);

public record TwitchPrimePaidUpgradeNotice(string Tier);

public record TwitchWatchStreakNotice(int StreakCount, int ChannelPointsAwarded);

/// <summary>
/// EventSub <c>channel.chat.notification</c> (v1): the chat-visible notices - subs, resubs, gifts,
/// raids, announcements, bits badge tiers, charity donations, watch streaks. <see cref="NoticeType"/>
/// is Twitch's raw value (including the <c>shared_chat_*</c> variants); the matching typed object is
/// set and the others are null. For <c>shared_chat_*</c> notices the typed object is the shared one
/// folded into the same property (e.g. <c>shared_chat_sub</c> fills <see cref="Sub"/>) and the
/// <c>Source*</c> fields say which channel it came from.
/// </summary>
public record TwitchChatNotification : TwitchEventBase
{
    public string ChatMessageId { get; init; } = "";
    public string NoticeType { get; init; } = "";

    /// <summary>The message Twitch shows in chat for the notice ("X subscribed at Tier 1.").</summary>
    public string SystemMessage { get; init; } = "";

    public string ChatterUserId { get; init; } = "";
    public string ChatterLogin { get; init; } = "";
    public string ChatterName { get; init; } = "";
    public bool ChatterIsAnonymous { get; init; }
    public string Color { get; init; } = "";
    public TwitchChatBadge[] Badges { get; init; } = [];

    /// <summary>Text the user attached (resub message, announcement body), empty when none.</summary>
    public string Text { get; init; } = "";
    public TwitchChatFragment[] Fragments { get; init; } = [];

    public TwitchSubNotice? Sub { get; init; }
    public TwitchResubNotice? Resub { get; init; }
    public TwitchSubGiftNotice? SubGift { get; init; }
    public TwitchCommunitySubGiftNotice? CommunitySubGift { get; init; }
    public TwitchGiftPaidUpgradeNotice? GiftPaidUpgrade { get; init; }
    public TwitchPrimePaidUpgradeNotice? PrimePaidUpgrade { get; init; }
    public TwitchPayItForwardNotice? PayItForward { get; init; }
    public TwitchRaidNotice? Raid { get; init; }
    public TwitchAnnouncementNotice? Announcement { get; init; }
    public TwitchBitsBadgeTierNotice? BitsBadgeTier { get; init; }
    public TwitchCharityDonationNotice? CharityDonation { get; init; }
    public TwitchWatchStreakNotice? WatchStreak { get; init; }

    public string? SourceBroadcasterUserId { get; init; }
    public string? SourceBroadcasterLogin { get; init; }
    public string? SourceMessageId { get; init; }
    public bool? IsSourceOnly { get; init; }
}

/// <summary>EventSub <c>channel.chat.message_delete</c> (v1): a moderator removed one message.</summary>
public record TwitchChatMessageDeleted : TwitchEventBase
{
    public string TargetUserId { get; init; } = "";
    public string TargetLogin { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string ChatMessageId { get; init; } = "";
}

/// <summary>EventSub <c>channel.chat.clear</c> (v1): a moderator cleared the whole chat.</summary>
public record TwitchChatCleared : TwitchEventBase;

/// <summary>EventSub <c>channel.chat.clear_user_messages</c> (v1): all of one user's messages were removed (a ban or timeout, as chat sees it).</summary>
public record TwitchChatUserMessagesCleared : TwitchEventBase
{
    public string TargetUserId { get; init; } = "";
    public string TargetLogin { get; init; } = "";
    public string TargetName { get; init; } = "";
}

/// <summary>EventSub <c>channel.chat_settings.update</c> (v1): chat modes changed.</summary>
public record TwitchChatSettingsUpdated : TwitchEventBase
{
    public bool EmoteMode { get; init; }
    public bool FollowerMode { get; init; }
    public int? FollowerModeDurationMinutes { get; init; }
    public bool SlowMode { get; init; }
    public int? SlowModeWaitTimeSeconds { get; init; }
    public bool SubscriberMode { get; init; }
    public bool UniqueChatMode { get; init; }
}
