using MassTransit;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Realtime.Video;

/// <summary>
/// Pulls a single channel's live HLS stream and forwards each new segment as a
/// <see cref="LiveVideoSegment"/> — best-effort, published directly via <see cref="IBus"/>
/// (NOT the outbox), with a short broker TTL. Stateless across channels: all per-stream state
/// lives in <see cref="RunAsync"/> locals, so one instance serves many concurrent captures.
/// </summary>
public class HlsCaptureLoop
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IBus _bus;
    private readonly RealtimeOptions _opts;
    private readonly ILogger<HlsCaptureLoop> _log;

    public HlsCaptureLoop(IHttpClientFactory httpFactory, IBus bus, IOptions<RealtimeOptions> opts, ILogger<HlsCaptureLoop> log)
    {
        _httpFactory = httpFactory;
        _bus = bus;
        _opts = opts.Value;
        _log = log;
    }

    public async Task RunAsync(string slug, string channelId, string masterUrl, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(RealtimeHttpClients.Hls);

        if (!RealtimeHttpClients.IsFetchableHttps(masterUrl) || !Uri.TryCreate(masterUrl, UriKind.Absolute, out var masterAbs))
        {
            _log.LogWarning("Capture {Slug}: playback URL is not an absolute https URL — skipping ({Url})", slug, masterUrl);
            return;
        }

        // Kick's live HLS lives on a streaming CDN (e.g. Amazon IVS), not kick.com — and segments
        // may be served from a *different* host than the manifest. Since every URL is derived from
        // the trusted playback URL's playlists, we require https + a size cap rather than pinning a
        // host (host-pinning silently drops cross-host segments). mediaHost is kept only for logging.
        var mediaHost = masterAbs.Host;

        _log.LogInformation("Capture {Slug}: pulling HLS from {Host}", slug, mediaHost);

        // Resolve to a media playlist (choose a variant if this is a master playlist).
        Uri mediaUri;
        long bandwidth = 0;
        var masterText = await GetStringAsync(http, masterAbs, ct);
        if (masterText is null) { _log.LogWarning("Capture {Slug}: master playlist fetch failed ({Host}) — see debug for HTTP status", slug, mediaHost); return; }

        if (M3U8.IsMaster(masterText))
        {
            var chosen = M3U8.ChooseVariant(M3U8.ParseMaster(masterText, masterAbs), _opts.Video.MaxBitrateKbps);
            if (chosen is null) { _log.LogWarning("Capture {Slug}: no variant in master playlist", slug); return; }
            mediaUri = chosen.Uri;
            bandwidth = chosen.Bandwidth;
            _log.LogInformation("Capture {Slug}: variant {Bw}bps {Res}", slug, chosen.Bandwidth, chosen.Resolution ?? "?");
        }
        else
        {
            mediaUri = masterAbs; // already a media playlist
        }

        long lastSeq = -1;
        Uri? lastMap = null;
        byte[]? initBytes = null;                 // cached fMP4 init (EXT-X-MAP) so we can re-emit it
        var initContentType = "video/mp4";
        var lastInitPublishedAt = DateTime.MinValue;
        var initInterval = TimeSpan.FromSeconds(Math.Max(2, _opts.Video.InitRepublishSeconds));
        var emptyPolls = 0;
        var forwarded = 0;
        long bytesTotal = 0;
        var lastLog = DateTime.MinValue; // → the first forwarded segment logs immediately, then throttled

        while (!ct.IsCancellationRequested)
        {
            var text = await GetStringAsync(http, mediaUri, ct);
            if (text is null)
            {
                if (++emptyPolls >= 5) { _log.LogInformation("Capture {Slug}: playlist gone — stopping", slug); return; }
                await DelayAsync(2, ct);
                continue;
            }
            emptyPolls = 0;

            var pl = M3U8.ParseMedia(text, mediaUri);

            if (pl.MapUri is not null)
            {
                // Fetch (and cache) the init only when it changes.
                if (pl.MapUri != lastMap)
                {
                    var fetched = await FetchBytesAsync(http, pl.MapUri, slug, ct);
                    if (fetched is { } f)
                    {
                        initBytes = f.Bytes;
                        initContentType = f.ContentType;
                        lastMap = pl.MapUri;
                        lastInitPublishedAt = DateTime.MinValue; // publish the new init immediately below
                        _log.LogInformation("Capture {Slug}: init segment (EXT-X-MAP) resolved, {Bytes} B — re-emitting every {Sec}s so mid-stream subscribers can bootstrap",
                            slug, f.Bytes.Length, _opts.Video.InitRepublishSeconds);
                    }
                }

                // Re-publish the cached init on change and then periodically, so any subscriber that
                // joins mid-stream can bootstrap a valid fMP4 file (the init is tiny).
                if (initBytes is not null && DateTime.UtcNow - lastInitPublishedAt >= initInterval)
                {
                    var ib = await PublishBytesAsync(slug, channelId, bandwidth, pl.MapUri, pl.MediaSequence, 0,
                        isInit: true, 0, initBytes, initContentType, ct);
                    if (ib > 0) { forwarded++; bytesTotal += ib; }
                    lastInitPublishedAt = DateTime.UtcNow;
                }
            }

            foreach (var seg in pl.Segments)
            {
                if (seg.Sequence <= lastSeq) continue;
                var sb = await ForwardAsync(http, slug, channelId, bandwidth, seg.Uri, seg.Sequence, seg.Duration, isInit: false, seg.DiscontinuitySequence, ct);
                if (sb > 0) { forwarded++; bytesTotal += sb; }
                lastSeq = seg.Sequence;
            }

            // Positive heartbeat that segments really are being published to the broker.
            if (forwarded > 0 && DateTime.UtcNow - lastLog >= TimeSpan.FromSeconds(10))
            {
                _log.LogInformation("Capture {Slug}: forwarded {Count} segment(s) ({Kb} KB total) → last seq {Seq}",
                    slug, forwarded, bytesTotal / 1024, lastSeq);
                lastLog = DateTime.UtcNow;
            }

            if (pl.EndList) { _log.LogInformation("Capture {Slug}: ENDLIST — stopping", slug); return; }

            var wait = pl.TargetDuration > 0 ? pl.TargetDuration / 2 : 2;
            await DelayAsync(Math.Max(1, wait), ct);
        }
    }

    /// <summary>Fetch + publish one segment. Returns the byte count published, or 0 if skipped/failed.</summary>
    private async Task<int> ForwardAsync(HttpClient http, string slug, string channelId, long bandwidth,
        Uri uri, long sequence, double duration, bool isInit, int discSeq, CancellationToken ct)
    {
        var fetched = await FetchBytesAsync(http, uri, slug, ct);
        if (fetched is not { } f) return 0;
        return await PublishBytesAsync(slug, channelId, bandwidth, uri, sequence, duration, isInit, discSeq, f.Bytes, f.ContentType, ct);
    }

    /// <summary>GET a segment/playlist's bytes + content type, or null on any skip/failure.</summary>
    private async Task<(byte[] Bytes, string ContentType)?> FetchBytesAsync(HttpClient http, Uri uri, string slug, CancellationToken ct)
    {
        if (!RealtimeHttpClients.IsFetchableHttps(uri.ToString()))
        {
            _log.LogDebug("Capture {Slug}: URL not absolute https ({Uri}) — skipping", slug, uri);
            return null;
        }
        try
        {
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) { _log.LogDebug("Capture {Slug}: segment {Uri} → {Status}", slug, uri, (int)resp.StatusCode); return null; }
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? GuessContentType(uri);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return (bytes, contentType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { _log.LogDebug(ex, "Capture {Slug}: segment fetch failed {Uri}", slug, uri); return null; }
    }

    /// <summary>Publish already-fetched bytes as a <see cref="LiveVideoSegment"/>. Returns bytes published, or 0 if dropped.</summary>
    private async Task<int> PublishBytesAsync(string slug, string channelId, long bandwidth, Uri uri,
        long sequence, double duration, bool isInit, int discSeq, byte[] bytes, string contentType, CancellationToken ct)
    {
        if (bytes.Length == 0) return 0;
        if (bytes.Length > _opts.Video.MaxSegmentBytes)
        {
            _log.LogWarning("Capture {Slug}: dropping {Bytes}B segment (> MaxSegmentBytes {Max})", slug, bytes.Length, _opts.Video.MaxSegmentBytes);
            return 0;
        }

        var msg = new LiveVideoSegment
        {
            BroadcasterSlug = slug,
            KickChannelId = channelId,
            MediaSequence = sequence,
            DiscontinuitySequence = discSeq,
            Duration = duration,
            IsInitSegment = isInit,
            VariantBandwidth = bandwidth,
            ContentType = contentType,
            SegmentUri = uri.ToString(),
            CapturedAt = DateTime.UtcNow,
            Data = bytes,
        };

        try
        {
            var ttl = TimeSpan.FromSeconds(Math.Max(5, _opts.Video.SegmentTtlSeconds));
            // Direct publish via IBus — bypasses the EF outbox (best-effort, ephemeral).
            await _bus.Publish(msg, ctx => ctx.TimeToLive = ttl, ct);
            return bytes.Length;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch (Exception ex) { _log.LogDebug(ex, "Capture {Slug}: segment publish failed {Uri}", slug, uri); return 0; }
    }

    private async Task<string?> GetStringAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(uri, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("playlist fetch {Uri} → HTTP {Status}", uri, (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { _log.LogDebug(ex, "playlist fetch failed {Uri}", uri); return null; }
    }

    private static string GuessContentType(Uri uri)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".ts")) return "video/mp2t";
        if (path.EndsWith(".m4s") || path.EndsWith(".mp4") || path.EndsWith(".m4v")) return "video/mp4";
        return "application/octet-stream";
    }

    private static async Task DelayAsync(double seconds, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); } catch (OperationCanceledException) { }
    }
}
