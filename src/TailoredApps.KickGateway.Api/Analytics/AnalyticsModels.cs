namespace TailoredApps.KickGateway.Api.Analytics;

// Response shapes of /api/analytics/*. Kept flat and self-describing because the main consumer
// is an LLM behind the MCP server: usernames next to ids, windows echoed back, counts explicit.
// All timestamps are UTC.

/// <summary>Time window a response covers. <c>From = null</c> means "since the beginning".</summary>
public sealed record AnalyticsWindow(DateTime? From, DateTime To);

public sealed record ChatterRef(string UserId, string Username);

public sealed record ProjectionStatus(
    string Source,
    DateTime? ProjectedThrough,
    long ProcessedInboxRows,
    long PendingInboxRows,
    DateTime? UpdatedAt);

public sealed record AnalyticsStatus(
    bool ProjectionEnabled,
    IReadOnlyList<ProjectionStatus> Projections,
    long ChatMessages,
    long ChatterEvents,
    DateTime? OldestMessageAt,
    DateTime? NewestMessageAt,
    IReadOnlyList<string>? ChannelsInScope);

public sealed record ChannelSummary(string Slug, int Messages, int Chatters, DateTime FirstMessageAt, DateTime LastMessageAt);

public sealed record ChannelList(AnalyticsWindow Window, IReadOnlyList<ChannelSummary> Channels);

public sealed record DailyActivity(DateTime Date, int Messages, int Chatters);

public sealed record ChannelEventTotals(
    int Follows,
    int NewSubscriptions,
    int Renewals,
    int GiftedSubscriptions,
    int KicksGifted,
    int RewardRedemptions,
    int Bans,
    int Timeouts,
    int MessagesDeleted);

/// <summary>A chatter's activity inside one channel + window.</summary>
public sealed record ChatterSummary(
    string UserId,
    string Username,
    int Messages,
    int ActiveDays,
    int RepliesSent,
    int RepliesReceived,
    int MentionsReceived,
    DateTime FirstMessageAt,
    DateTime LastMessageAt,
    DateTime FirstSeenInChannelAt,
    bool IsNewInWindow);

public sealed record ChannelOverview(
    string Slug,
    AnalyticsWindow Window,
    int Messages,
    int Chatters,
    int NewChatters,
    int ReturningChatters,
    double ReplyShare,
    double MentionShare,
    int[] MessagesByHourUtc,
    IReadOnlyList<DailyActivity> Daily,
    IReadOnlyList<ChatterSummary> TopChatters,
    ChannelEventTotals Events);

public sealed record ChatterPage(string Slug, AnalyticsWindow Window, string Sort, int Total, int Offset, IReadOnlyList<ChatterSummary> Chatters);

public sealed record ChatterSearchHit(string UserId, string Username, int Messages, DateTime LastMessageAt);

// === interaction graph ===

public sealed record GraphSummaryView(
    int Messages,
    int Chatters,
    int ConnectedChatters,
    int Edges,
    double Density,
    double Reciprocity,
    double Modularity,
    IReadOnlyList<string> TopHubs,
    IReadOnlyList<string> TopInitiators,
    IReadOnlyList<string> TopConnectors);

public sealed record GraphEdgeView(
    string From,
    string FromUsername,
    string To,
    string ToUsername,
    int Replies,
    int Mentions,
    int Gifts,
    double Proximity,
    double Weight);

public sealed record GraphCommunityView(int Id, int Size, IReadOnlyList<string> TopMembers, double InternalWeight, double Cohesion);

public sealed record InteractionGraphView(
    string Slug,
    AnalyticsWindow Window,
    bool Truncated,
    GraphSummaryView Summary,
    IReadOnlyList<GraphNodeResult> Nodes,
    IReadOnlyList<GraphEdgeView> Edges,
    IReadOnlyList<GraphCommunityView> Communities,
    GraphOptions Weights,
    string Legend);

// === chatter profile ===

public sealed record MessageView(
    string MessageId,
    string Channel,
    DateTime CreatedAt,
    string UserId,
    string Username,
    string Content,
    string? ReplyToMessageId,
    string? ReplyToUserId,
    string? ReplyToUsername,
    bool Deleted = false);

public sealed record ChatterActivity(
    int Messages,
    int SampledMessages,
    DateTime? FirstSeenAt,
    DateTime? LastSeenAt,
    int ActiveDays,
    int Sessions,
    double MessagesPerActiveDay,
    double AvgSessionMinutes,
    int[] MessagesByHourUtc,
    int[] MessagesByWeekdayUtc,
    IReadOnlyList<int> PeakHoursUtc);

public sealed record ChannelPresence(
    string Slug,
    int Messages,
    DateTime FirstMessageAt,
    DateTime LastMessageAt,
    IReadOnlyList<ChatBadge> Badges,
    int? SubscriberMonths,
    IReadOnlyList<string> Roles);

public sealed record PartnerView(
    string? UserId,
    string Username,
    double Weight,
    int RepliesTo,
    int RepliesFrom,
    int MentionsTo,
    int MentionsFrom,
    int GiftsTo,
    int GiftsFrom);

public sealed record ChatterSocial(
    int DistinctPartners,
    int RepliesSent,
    int RepliesReceived,
    int MentionsSent,
    int MentionsReceived,
    IReadOnlyList<PartnerView> TopPartners);

public sealed record ChannelFollowView(string Channel, DateTime At);

public sealed record SubscriptionView(string Channel, string Kind, int Months, DateTime At);

public sealed record ChatterSupport(
    IReadOnlyList<ChannelFollowView> Follows,
    IReadOnlyList<SubscriptionView> Subscriptions,
    int GiftedSubsGiven,
    int GiftedSubsReceived,
    int KicksGiven,
    IReadOnlyList<TermCount> Redemptions);

public sealed record ModerationView(
    string Channel,
    string Kind,
    string Direction,
    DateTime At,
    string UserId,
    string Username,
    string? ModeratorUsername,
    string? Reason,
    DateTime? ExpiresAt);

public sealed record ChatterModeration(
    int BansReceived,
    int TimeoutsReceived,
    int MessagesDeleted,
    int ActionsIssued,
    IReadOnlyList<ModerationView> Recent);

public sealed record ChatterProfile(
    string UserId,
    string Username,
    IReadOnlyList<string> KnownUsernames,
    string? OwnChannelSlug,
    bool IsVerified,
    string? Color,
    AnalyticsWindow Window,
    string? Channel,
    ChatterActivity Activity,
    IReadOnlyList<ChannelPresence> Channels,
    ChatStyle Style,
    ChatterSocial Social,
    ChatterSupport Support,
    ChatterModeration Moderation,
    IReadOnlyList<MessageView> RecentMessages);

// === relationship ===

public sealed record DirectionStats(
    int Replies,
    int Mentions,
    int GiftedSubs,
    double Weight,
    double? MedianReplyLatencySeconds,
    DateTime? LastInteractionAt);

public sealed record ExchangeView(string Kind, MessageView Message, MessageView? InReplyTo);

public sealed record ChatterRelationship(
    ChatterRef A,
    ChatterRef B,
    AnalyticsWindow Window,
    string? Channel,
    DirectionStats AToB,
    DirectionStats BToA,
    string Balance,
    IReadOnlyList<string> SharedChannels,
    int ActiveDaysA,
    int ActiveDaysB,
    int SharedActiveDays,
    IReadOnlyList<ModerationView> ModerationBetween,
    IReadOnlyList<ExchangeView> Exchanges);

// === messages ===

public sealed record MessagePage(IReadOnlyList<MessageView> Messages, string? NextCursor);

public sealed record MessageContext(
    MessageView Message,
    IReadOnlyList<MessageView> ReplyChain,
    IReadOnlyList<MessageView> Replies,
    IReadOnlyList<MessageView> Before,
    IReadOnlyList<MessageView> After);
