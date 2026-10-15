using MassTransit;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Twitch.Playback;
using TailoredApps.KickGateway.Telemetry;
using TailoredApps.TwitchGateway.Contracts.Media;

namespace TailoredApps.TwitchGateway.Capture;

/// <summary>
/// One channel's live capture: playback token -> usher master -> the chosen rendition's media playlist,
/// polled once per segment; every new segment is fetched and published as <see cref="TwitchAudioSegment"/>
/// (direct <see cref="IBus"/> publish with a short TTL - no inbox/outbox, segments are ephemeral). Segments
/// inside a stitched ad break are skipped and the discontinuity counter bumped so the transcriber resets
/// its decoder instead of hearing the ad. Ends when the playlist 404s (offline), stops advancing, or the
/// token cannot be renewed; the capture service restarts it on the next live check if the channel is still up.
/// </summary>
public sealed class TwitchAudioCaptureLoop
{
    private readonly ITwitchPlaybackClient _playback;
    private readonly IBus _bus;
    private readonly TwitchCaptureOptions _opts;
    private readonly ILogger<TwitchAudioCaptureLoop> _log;

    public TwitchAudioCaptureLoop(ITwitchPlaybackClient playback, IBus bus, IOptions<TwitchCaptureOptions> opts, ILogger<TwitchAudioCaptureLoop> log)
    {
        _playback = playback;
        _bus = bus;
        _opts = opts.Value;
        _log = log;
    }

    public async Task RunAsync(string login, string userId, string streamId, CancellationToken ct)
    {
        var access = await _playback.GetLivePlaybackAccessAsync(login, ct);
        if (access is null)
        {
            _log.LogWarning("[{Login}] no playback token - not capturing", login);
            return;
        }

        var master = await _playback.GetPlaylistAsync(_playback.BuildMasterPlaylistUri(login, access), ct);
        if (master.IsOffline)
        {
            _log.LogInformation("[{Login}] usher says offline (no transcode) - nothing to capture", login);
            return;
        }
        if (!master.IsSuccess || !TwitchHls.IsMaster(master.Body))
        {
            _log.LogWarning("[{Login}] master playlist HTTP {Status}: {Body}", login, master.StatusCode, Truncate(master.Body, 160));
            return;
        }

        var variants = TwitchHls.ParseMaster(master.Body, master.FinalUri);
        var variant = TwitchHls.ChooseRendition(variants, _opts.Rendition, _opts.MaxBitrateKbps);
        if (variant is null)
        {
            _log.LogWarning("[{Login}] master playlist has no renditions", login);
            return;
        }
        _log.LogInformation("[{Login}] capturing rendition {Name} ({Kbps} kb/s, {Codecs}) of stream {Stream}", login, variant.Name, variant.Bandwidth / 1000, variant.Codecs, streamId);

        long lastSeq = -1;
        var adBumps = 0;
        var inAd = false;
        var published = 0L;
        var skippedAds = 0L;
        var lastAdvance = DateTime.UtcNow;
        var pollDelay = TimeSpan.FromSeconds(2);
        var started = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var media = await _playback.GetPlaylistAsync(variant.Uri, ct);
            if (media.IsOffline)
            {
                _log.LogInformation("[{Login}] media playlist gone (404) - stream ended after {Min:F0} min, {Published} segment(s) published, {Ads} ad segment(s) skipped",
                    login, (DateTime.UtcNow - started).TotalMinutes, published, skippedAds);
                return;
            }
            if (media.IsTokenRejected)
            {
                // The signed playlist URL expired (tokens last ~20 min when Twitch decides to enforce it): start over with a fresh token.
                _log.LogInformation("[{Login}] playlist token rejected (HTTP {Status}) - renewing", login, media.StatusCode);
                access = await _playback.GetLivePlaybackAccessAsync(login, ct);
                if (access is null) return;
                master = await _playback.GetPlaylistAsync(_playback.BuildMasterPlaylistUri(login, access), ct);
                if (!master.IsSuccess) return;
                var fresh = TwitchHls.ChooseRendition(TwitchHls.ParseMaster(master.Body, master.FinalUri), _opts.Rendition, _opts.MaxBitrateKbps);
                if (fresh is null) return;
                variant = fresh;
                continue;
            }
            if (!media.IsSuccess)
            {
                _log.LogDebug("[{Login}] media playlist HTTP {Status}; retrying", login, media.StatusCode);
                await DelayAsync(pollDelay, ct);
                if (DateTime.UtcNow - lastAdvance > TimeSpan.FromSeconds(_opts.IdleStopSeconds)) return;
                continue;
            }

            var playlist = TwitchHls.ParseMedia(media.Body, media.FinalUri);
            var any = false;
            foreach (var seg in playlist.Segments)
            {
                if (seg.Sequence <= lastSeq) continue;
                lastSeq = seg.Sequence;
                any = true;
                if (seg.Duration > 0) pollDelay = TimeSpan.FromMilliseconds(Math.Max(1000, _opts.PollIntervalMs > 0 ? _opts.PollIntervalMs : seg.Duration * 1000));

                if (_opts.SkipAds && seg.IsAd)
                {
                    skippedAds++;
                    TwitchGatewayMetrics.RecordAudioSegment("ad_skipped");
                    if (!inAd)
                    {
                        inAd = true;
                        _log.LogInformation("[{Login}] ad break started at sequence {Seq} - skipping until it ends", login, seg.Sequence);
                    }
                    continue;
                }
                if (inAd)
                {
                    inAd = false;
                    adBumps++; // the transcriber sees a discontinuity and resets its decoder
                    _log.LogInformation("[{Login}] ad break over at sequence {Seq}", login, seg.Sequence);
                }

                var data = await _playback.GetSegmentAsync(seg.Uri, _opts.MaxSegmentBytes, ct);
                if (data is null)
                {
                    TwitchGatewayMetrics.RecordAudioSegment("fetch_failed");
                    _log.LogDebug("[{Login}] segment {Seq} could not be fetched - skipped", login, seg.Sequence);
                    continue;
                }

                var now = DateTime.UtcNow;
                var msg = new TwitchAudioSegment
                {
                    BroadcasterLogin = login,
                    BroadcasterUserId = userId,
                    StreamId = streamId,
                    Rendition = variant.Name,
                    MediaSequence = seg.Sequence,
                    DiscontinuitySequence = seg.DiscontinuitySequence + adBumps,
                    Duration = seg.Duration,
                    ContentType = "video/mp2t",
                    SegmentUri = seg.Uri.ToString(),
                    ProgramDateTime = seg.ProgramDateTime,
                    CapturedAt = now,
                    Data = data,
                };
                var ttl = TimeSpan.FromSeconds(Math.Max(5, _opts.SegmentTtlSeconds));
                await _bus.Publish(msg, ctx => ctx.TimeToLive = ttl, ct);
                published++;
                TwitchGatewayMetrics.RecordAudioSegment("published");
                if (published % 300 == 0)
                    _log.LogInformation("[{Login}] {Published} segment(s) published, {Ads} ad segment(s) skipped, last sequence {Seq}", login, published, skippedAds, seg.Sequence);
            }

            if (any) lastAdvance = DateTime.UtcNow;
            else if (DateTime.UtcNow - lastAdvance > TimeSpan.FromSeconds(_opts.IdleStopSeconds))
            {
                _log.LogInformation("[{Login}] playlist stopped advancing for {Sec}s - stopping capture ({Published} published)", login, _opts.IdleStopSeconds, published);
                return;
            }
            if (playlist.EndList)
            {
                _log.LogInformation("[{Login}] playlist ended (EXT-X-ENDLIST) - {Published} segment(s) published", login, published);
                return;
            }
            await DelayAsync(pollDelay, ct);
        }
    }

    private static async Task DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); } catch (OperationCanceledException) { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
