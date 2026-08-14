namespace TailoredApps.KickGateway.Contracts.Realtime;

// Contracts sourced from the `chatrooms.{id}.v2` Pusher channel.

/// <summary>A chat badge as carried in the sender identity (subscriber, moderator, og, vip, …).</summary>
public record RealtimeChatBadge(string Type, string Text, int Count);

/// <summary><c>App\Events\ChatMessageEvent</c> — a chat message (or reply).</summary>
public record RealtimeChatMessage : KickRealtimeEventBase
{
    public string MessageId { get; init; } = "";
    public string Content { get; init; } = "";
    /// <summary>Kick message type: <c>message</c>, <c>reply</c>, <c>celebration</c>, …</summary>
    public string MessageType { get; init; } = "";
    public DateTime? CreatedAt { get; init; }
    public string SenderId { get; init; } = "";
    public string SenderUsername { get; init; } = "";
    public string SenderSlug { get; init; } = "";
    public string? SenderColor { get; init; }
    public RealtimeChatBadge[] SenderBadges { get; init; } = Array.Empty<RealtimeChatBadge>();
    /// <summary>Set when <see cref="MessageType"/> is a reply — the message being replied to.</summary>
    public string? ReplyToMessageId { get; init; }
    public string? ReplyToSenderUsername { get; init; }
}

/// <summary><c>App\Events\MessageDeletedEvent</c> — a single chat message was removed.</summary>
public record RealtimeMessageDeleted : KickRealtimeEventBase
{
    /// <summary>Id of the deleted message.</summary>
    public string MessageId { get; init; } = "";
    public bool AiModerated { get; init; }
}

/// <summary><c>App\Events\UserBannedEvent</c> — a permanent ban or a timeout.</summary>
public record RealtimeUserBanned : KickRealtimeEventBase
{
    public string BannedUserId { get; init; } = "";
    public string BannedUsername { get; init; } = "";
    public string BannedSlug { get; init; } = "";
    public string ModeratorUserId { get; init; } = "";
    public string ModeratorUsername { get; init; } = "";
    /// <summary>True = permanent ban, false = timeout (see <see cref="ExpiresAt"/>).</summary>
    public bool Permanent { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary><c>App\Events\UserUnbannedEvent</c> — a ban/timeout was lifted.</summary>
public record RealtimeUserUnbanned : KickRealtimeEventBase
{
    public string UserId { get; init; } = "";
    public string Username { get; init; } = "";
    public string Slug { get; init; } = "";
    public string ModeratorUsername { get; init; } = "";
    public bool Permanent { get; init; }
}

/// <summary><c>App\Events\PinnedMessageCreatedEvent</c> — a message was pinned.</summary>
public record RealtimePinnedMessageCreated : KickRealtimeEventBase
{
    public string MessageId { get; init; } = "";
    public string Content { get; init; } = "";
    public string SenderUsername { get; init; } = "";
    public string PinnedByUsername { get; init; } = "";
    public int? DurationSeconds { get; init; }
}

/// <summary><c>App\Events\PinnedMessageDeletedEvent</c> — the pinned message was unpinned.</summary>
public record RealtimePinnedMessageDeleted : KickRealtimeEventBase;

/// <summary>A single option in a chat poll.</summary>
public record RealtimePollOption(int Id, string Label, int Votes);

/// <summary><c>App\Events\PollUpdateEvent</c> — a poll was created or received votes.</summary>
public record RealtimePollUpdate : KickRealtimeEventBase
{
    public string Title { get; init; } = "";
    public RealtimePollOption[] Options { get; init; } = Array.Empty<RealtimePollOption>();
    public int DurationSeconds { get; init; }
    public int RemainingSeconds { get; init; }
}

/// <summary><c>App\Events\PollDeleteEvent</c> — a poll ended/was removed.</summary>
public record RealtimePollDelete : KickRealtimeEventBase;

/// <summary><c>App\Events\ChatroomUpdatedEvent</c> — chatroom mode changes (slow/followers/sub/emote).</summary>
public record RealtimeChatroomUpdated : KickRealtimeEventBase
{
    public bool SlowModeEnabled { get; init; }
    public int SlowModeIntervalSeconds { get; init; }
    public bool SubscribersOnly { get; init; }
    public bool FollowersOnly { get; init; }
    public int FollowersOnlyMinMinutes { get; init; }
    public bool EmotesOnly { get; init; }
}

/// <summary><c>App\Events\ChatroomClearEvent</c> — the moderator cleared chat.</summary>
public record RealtimeChatroomClear : KickRealtimeEventBase;
