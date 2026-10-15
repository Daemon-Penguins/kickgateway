namespace TailoredApps.Integrations.Twitch.Helix;

public sealed record TwitchUser(string Id, string Login, string DisplayName, string ProfileImageUrl, string BroadcasterType);

/// <summary>One item of Helix <c>GET /streams</c> - present only while the channel is live.</summary>
public sealed record TwitchStream(
    string Id,
    string UserId,
    string UserLogin,
    string UserName,
    string GameId,
    string GameName,
    string Type,
    string Title,
    int ViewerCount,
    DateTime StartedAt,
    string Language,
    bool IsMature);

/// <summary>One item of Helix <c>GET /videos</c>. <see cref="StreamId"/> links an archive (VOD) to the broadcast it recorded.</summary>
public sealed record TwitchVideo(
    string Id,
    string StreamId,
    string UserId,
    string UserLogin,
    string Title,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    string Url,
    string Type,
    string Duration);

public sealed record EventSubSubscription(string Id, string Status, string Type, string Version, int Cost, string ConditionJson, string? SessionId, DateTime CreatedAt);

/// <summary>Outcome of <c>POST /eventsub/subscriptions</c>. <see cref="TotalCost"/>/<see cref="MaxTotalCost"/> are Twitch's budget counters.</summary>
public sealed record EventSubCreateResult(bool Success, EventSubSubscription? Subscription, int StatusCode, string? Error, int TotalCost, int MaxTotalCost)
{
    /// <summary>HTTP 429 or a budget/limit refusal - the caller should back off rather than retry immediately.</summary>
    public bool IsRateLimited => StatusCode == 429;

    /// <summary>HTTP 401/403 - the token is bad or lacks the scope; retrying with the same token is pointless.</summary>
    public bool IsAuthFailure => StatusCode is 401 or 403;
}

/// <summary>
/// The Helix calls the gateway needs. Every call takes the bearer token explicitly (bot user token or app
/// token) because the same client serves processes with different credentials.
/// </summary>
public interface ITwitchHelixClient
{
    /// <summary>Up to 100 logins per request (batched internally). Unknown logins are simply absent.</summary>
    Task<IReadOnlyList<TwitchUser>> GetUsersByLoginAsync(string accessToken, IEnumerable<string> logins, CancellationToken ct = default);

    Task<IReadOnlyList<TwitchUser>> GetUsersByIdAsync(string accessToken, IEnumerable<string> userIds, CancellationToken ct = default);

    /// <summary>Live streams among the given user ids (batched by 100; offline channels are absent from the result).</summary>
    Task<IReadOnlyList<TwitchStream>> GetStreamsAsync(string accessToken, IEnumerable<string> userIds, CancellationToken ct = default);

    /// <summary>
    /// The user's most recent videos (<c>GET /videos?user_id=</c>), newest first. <paramref name="type"/> is
    /// Twitch's filter (<c>archive</c> = VODs of past and running broadcasts, <c>highlight</c>, <c>upload</c>,
    /// <c>all</c>); <paramref name="first"/> caps the page (1-100).
    /// </summary>
    Task<IReadOnlyList<TwitchVideo>> GetVideosAsync(string accessToken, string userId, string type = "archive", int first = 5, CancellationToken ct = default);

    Task<EventSubCreateResult> CreateEventSubSubscriptionAsync(string accessToken, string type, string version,
        IReadOnlyDictionary<string, string> condition, string websocketSessionId, CancellationToken ct = default);

    /// <summary>All subscriptions of the token's client id (+ user for user tokens), optionally filtered by status (<c>enabled</c>, <c>websocket_disconnected</c>, ...).</summary>
    Task<IReadOnlyList<EventSubSubscription>> ListEventSubSubscriptionsAsync(string accessToken, string? status = null, CancellationToken ct = default);

    Task<bool> DeleteEventSubSubscriptionAsync(string accessToken, string subscriptionId, CancellationToken ct = default);
}
