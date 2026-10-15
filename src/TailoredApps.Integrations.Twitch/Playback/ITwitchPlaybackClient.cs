namespace TailoredApps.Integrations.Twitch.Playback;

/// <summary>A live playback access token from GQL: the signed token Twitch's player hands to usher.</summary>
public sealed record TwitchPlaybackAccess(string Token, string Signature, DateTime ObtainedAt, DateTime? ExpiresAt, string ChannelId, bool ShowAds)
{
    /// <summary>True when usher should still accept it (Twitch tokens last ~20 min; refresh a minute early).</summary>
    public bool IsFresh(DateTime now) => ExpiresAt is null ? now - ObtainedAt < TimeSpan.FromMinutes(15) : now < ExpiresAt.Value - TimeSpan.FromMinutes(1);
}

/// <summary>Result of fetching a playlist: HTTP status + body (the master or media m3u8), or an error.</summary>
public sealed record TwitchPlaylistResponse(int StatusCode, string Body, Uri FinalUri)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
    public bool IsOffline => StatusCode == 404;
    public bool IsTokenRejected => StatusCode is 401 or 403;
}

/// <summary>
/// What Twitch's own web player does to get a live stream: a <c>PlaybackAccessToken</c> from GQL with
/// the player's public client id, then the usher master playlist with that token + signature. No Twitch
/// developer credentials involved; the gateway never logs in. Segment requests go through
/// <see cref="GetAsync"/> with the same browser headers.
/// </summary>
public interface ITwitchPlaybackClient
{
    /// <summary>Null when GQL refused (channel does not exist, or Twitch rotated the persisted-query hash - watch the logs).</summary>
    Task<TwitchPlaybackAccess?> GetLivePlaybackAccessAsync(string login, CancellationToken ct = default);

    /// <summary>The usher master playlist URL for <paramref name="login"/> (random <c>p</c> per call, audio-only allowed).</summary>
    Uri BuildMasterPlaylistUri(string login, TwitchPlaybackAccess access);

    /// <summary>Fetches a playlist (master or media). 404 = the channel is offline / has no transcode.</summary>
    Task<TwitchPlaylistResponse> GetPlaylistAsync(Uri uri, CancellationToken ct = default);

    /// <summary>Fetches a media segment's bytes; null on any failure (the capture loop skips and moves on).</summary>
    Task<byte[]?> GetSegmentAsync(Uri uri, int maxBytes, CancellationToken ct = default);
}
