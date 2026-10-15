using MassTransit;
using TailoredApps.Integrations.Twitch.Auth;
using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.TwitchGateway.Contracts.Channel;

namespace TailoredApps.KickGateway.Api.Channels;

/// <summary>
/// Answers <see cref="TwitchStreamStatsRequested"/> from Helix with the gateway's app token - the Twitch
/// counterpart of <see cref="ChannelStatsConsumer"/>. Three calls: <c>GET /users</c> (login → id, profile),
/// <c>GET /streams</c> (live state, viewers, title, category) and, while live, <c>GET /videos?type=archive</c>
/// to find the VOD of the broadcast in progress (matched on <c>stream_id</c>, best-effort with a short
/// budget so a slow videos call never delays the viewer count). Publishes the snapshot and responds when
/// invoked as a request.
/// </summary>
public class TwitchStreamStatsConsumer(
    ITwitchAuthClient auth,
    ITwitchHelixClient helix,
    ILogger<TwitchStreamStatsConsumer> log) : IConsumer<TwitchStreamStatsRequested>
{
    private static readonly TimeSpan LiveVodLookupTimeout = TimeSpan.FromSeconds(10);

    public async Task Consume(ConsumeContext<TwitchStreamStatsRequested> context)
    {
        var login = (context.Message.BroadcasterLogin ?? "").Trim().ToLowerInvariant();
        var stats = await BuildAsync(login, context.CancellationToken);
        await context.Publish(stats);
        if (context.RequestId is not null)
            await context.RespondAsync(stats);
    }

    private async Task<TwitchStreamStats> BuildAsync(string login, CancellationToken ct)
    {
        if (login.Length == 0)
            return Failed("", "missing login");

        var token = await auth.GetAppAccessTokenAsync(ct);
        if (token is null)
            return Failed(login, "Twitch developer application is not configured on the gateway (Twitch:ClientId / Twitch:ClientSecret)");

        try
        {
            var user = (await helix.GetUsersByLoginAsync(token, [login], ct)).FirstOrDefault();
            if (user is null)
                return Failed(login, "channel not found");

            var stream = (await helix.GetStreamsAsync(token, [user.Id], ct)).FirstOrDefault();
            var videos = stream is null ? [] : await LiveVideosAsync(token, user.Id, ct);

            log.LogInformation("Twitch stream stats {Login}: live={Live} viewers={Viewers}", login, stream is not null, stream?.ViewerCount ?? 0);
            return Build(login, user, stream, videos, DateTime.UtcNow);
        }
        catch (TwitchUnauthorizedException ex)
        {
            log.LogWarning(ex, "Twitch stream stats for {Login}: Helix rejected the app token", login);
            return Failed(login, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Twitch stream stats fetch threw for {Login}", login);
            return Failed(login, ex.Message);
        }
    }

    /// <summary>Pure mapping of the three Helix answers into the contract - the VOD is the archive whose <c>stream_id</c> is the live stream's.</summary>
    public static TwitchStreamStats Build(string login, TwitchUser user, TwitchStream? stream, IReadOnlyList<TwitchVideo> videos, DateTime now)
    {
        var vod = stream is null
            ? null
            : videos.FirstOrDefault(v => v.StreamId.Length > 0 && string.Equals(v.StreamId, stream.Id, StringComparison.Ordinal));

        return new TwitchStreamStats
        {
            BroadcasterLogin = user.Login.Length > 0 ? user.Login.ToLowerInvariant() : login,
            BroadcasterUserId = user.Id,
            DisplayName = user.DisplayName,
            ProfileImageUrl = NullIfBlank(user.ProfileImageUrl),
            BroadcasterType = user.BroadcasterType,
            FetchedAt = now,
            Success = true,
            IsLive = stream is not null,
            StreamId = stream?.Id,
            ViewerCount = stream?.ViewerCount ?? 0,
            Title = NullIfBlank(stream?.Title),
            CategoryId = NullIfBlank(stream?.GameId),
            CategoryName = NullIfBlank(stream?.GameName),
            Language = NullIfBlank(stream?.Language),
            StartedAt = stream?.StartedAt,
            IsMature = stream?.IsMature ?? false,
            LiveVodId = vod?.Id,
            LiveVodUrl = TwitchWatchUrls.Vod(vod?.Id),
            LiveVodStartedAt = vod?.CreatedAt,
        };
    }

    private async Task<IReadOnlyList<TwitchVideo>> LiveVideosAsync(string token, string userId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LiveVodLookupTimeout);
            return await helix.GetVideosAsync(token, userId, "archive", 5, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogDebug("Twitch live VOD lookup for user {UserId} timed out", userId);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Twitch live VOD lookup for user {UserId} failed", userId);
            return [];
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static TwitchStreamStats Failed(string login, string error) => new()
    {
        BroadcasterLogin = login,
        FetchedAt = DateTime.UtcNow,
        Success = false,
        Error = error,
    };
}
