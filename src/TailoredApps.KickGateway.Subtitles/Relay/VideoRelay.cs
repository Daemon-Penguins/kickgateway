using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subtitles.Relay;

/// <summary>Per-channel relay numbers for the status endpoint and the stats log.</summary>
public sealed record RelayChannelStats(string Slug, int Segments, double BufferedSeconds, long BufferedBytes, int Viewers, long ServedBytes, long ServedSegments);

/// <summary>
/// Keeps the last <see cref="RelayOptions.BufferSeconds"/> of each relayed channel's video in memory —
/// the very segments the gateway already pulls for transcription (<see cref="LiveVideoSegment"/>) — and
/// serves them back as a live HLS playlist a viewer's player can position itself <i>behind</i> the
/// live edge on. Segments are renumbered contiguously (Kick's media sequence can have holes when the
/// capture dropped one; a hole or a Kick discontinuity becomes <c>#EXT-X-DISCONTINUITY</c>) and each
/// carries <c>#EXT-X-PROGRAM-DATE-TIME</c> = <c>CapturedAt − Duration</c>, the same clock the
/// transcriber stamps transcripts with — which is what lets the page show a caption exactly when its
/// words play. Viewers are counted per channel (playlist polls carry a client id) and capped, because
/// every viewer costs the full bitrate in outbound transfer.
/// </summary>
public sealed class VideoRelay
{
    private sealed class ChannelBuffer
    {
        public readonly LinkedList<BufferedSegment> Segments = new();
        public BufferedSegment? Init;
        public long NextLocalSequence = 1;
        public long LastMediaSequence = -1;
        public int LastKickDiscontinuity = int.MinValue;
        public int DiscontinuityIndex;
        public long BufferedBytes;
        public string? CachedPlaylist;
        public string? CachedSuffix;
        public readonly Dictionary<string, DateTime> Viewers = new(StringComparer.Ordinal);
        public long ServedBytes;
        public long ServedSegments;
    }

    private readonly RelayOptions _opts;
    private readonly string[] _channels;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ChannelBuffer> _buffers = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public VideoRelay(RelayOptions options, SubtitlesOptions site, TimeProvider? time = null)
    {
        _opts = options;
        _channels = options.NormalizedChannels.Length > 0 ? options.NormalizedChannels : site.NormalizedChannels;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Slugs the relay buffers (the binding list for the video queue). Empty = every channel.</summary>
    public IReadOnlyList<string> Channels => _channels;

    public bool Accepts(string slug) => _channels.Length == 0 || _channels.Contains(slug, StringComparer.Ordinal);

    /// <summary>Stores one gateway segment. False when the slug is unusable or not relayed.</summary>
    public bool Ingest(LiveVideoSegment seg)
    {
        if (!SubtitlesOptions.TryNormalizeSlug(seg.BroadcasterSlug, out var slug) || !Accepts(slug)) return false;
        if (seg.Data is null || seg.Data.Length == 0) return false;

        var now = _time.GetUtcNow().UtcDateTime;
        var container = seg.DetectContainer();
        lock (_gate)
        {
            var buffer = Get(slug);
            if (seg.IsInitSegment)
            {
                buffer.Init = new BufferedSegment(0, seg.MediaSequence, buffer.DiscontinuityIndex, 0, seg.CapturedAt, now, container, seg.Data);
                buffer.CachedPlaylist = null;
                return true;
            }

            // Kick's own discontinuity counter moving, or a hole in its media sequence (a dropped
            // segment), both mean the next bytes don't continue the previous ones.
            var hole = buffer.LastMediaSequence >= 0 && seg.MediaSequence != buffer.LastMediaSequence + 1;
            var kickDiscontinuity = buffer.LastKickDiscontinuity != int.MinValue && seg.DiscontinuitySequence != buffer.LastKickDiscontinuity;
            if (buffer.Segments.Count > 0 && (hole || kickDiscontinuity)) buffer.DiscontinuityIndex++;
            buffer.LastMediaSequence = seg.MediaSequence;
            buffer.LastKickDiscontinuity = seg.DiscontinuitySequence;

            var duration = seg.Duration > 0 ? seg.Duration : 2;
            var item = new BufferedSegment(
                buffer.NextLocalSequence++, seg.MediaSequence, buffer.DiscontinuityIndex, duration,
                seg.CapturedAt.AddSeconds(-duration), now, container, seg.Data);
            buffer.Segments.AddLast(item);
            buffer.BufferedBytes += item.Data.Length;
            Trim(buffer);
            buffer.CachedPlaylist = null;
            return true;
        }
    }

    /// <summary>The channel's live playlist, or null while nothing is buffered. Cached until the next segment.</summary>
    public string? Playlist(string slug, string querySuffix)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(slug, out var buffer) || buffer.Segments.Count == 0) return null;
            if (buffer.CachedPlaylist is null || buffer.CachedSuffix != querySuffix)
            {
                buffer.CachedPlaylist = HlsPlaylist.Build(buffer.Segments.ToArray(), buffer.Init is not null, querySuffix);
                buffer.CachedSuffix = querySuffix;
            }
            return buffer.CachedPlaylist;
        }
    }

    public BufferedSegment? Segment(string slug, long localSequence)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(slug, out var buffer)) return null;
            return buffer.Segments.FirstOrDefault(s => s.LocalSequence == localSequence);
        }
    }

    public BufferedSegment? Init(string slug)
    {
        lock (_gate) return _buffers.TryGetValue(slug, out var buffer) ? buffer.Init : null;
    }

    /// <summary>Registers / refreshes a viewer; false when the channel is at its viewer cap.</summary>
    public bool TouchViewer(string slug, string clientId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            var buffer = Get(slug);
            PruneViewers(buffer, now);
            if (buffer.Viewers.ContainsKey(clientId))
            {
                buffer.Viewers[clientId] = now;
                return true;
            }
            if (buffer.Viewers.Count >= _opts.MaxViewersPerChannel) return false;
            buffer.Viewers[clientId] = now;
            return true;
        }
    }

    public void CountServed(string slug, long bytes)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(slug, out var buffer)) return;
            buffer.ServedBytes += bytes;
            buffer.ServedSegments++;
        }
    }

    public RelayChannelStats Stats(string slug)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (!_buffers.TryGetValue(slug, out var b)) return new RelayChannelStats(slug, 0, 0, 0, 0, 0, 0);
            PruneViewers(b, now);
            return new RelayChannelStats(slug, b.Segments.Count, b.Segments.Sum(s => s.Duration), b.BufferedBytes, b.Viewers.Count, b.ServedBytes, b.ServedSegments);
        }
    }

    public IReadOnlyList<RelayChannelStats> Snapshot()
    {
        lock (_gate) return _buffers.Keys.OrderBy(k => k).Select(Stats).ToArray();
    }

    private ChannelBuffer Get(string slug)
    {
        if (!_buffers.TryGetValue(slug, out var buffer))
            _buffers[slug] = buffer = new ChannelBuffer();
        return buffer;
    }

    private void Trim(ChannelBuffer buffer)
    {
        var maxBytes = (long)_opts.MaxBufferMegabytesPerChannel * 1024 * 1024;
        while (buffer.Segments.Count > 1
               && (buffer.Segments.Sum(s => s.Duration) > _opts.BufferSeconds || buffer.BufferedBytes > maxBytes))
        {
            buffer.BufferedBytes -= buffer.Segments.First!.Value.Data.Length;
            buffer.Segments.RemoveFirst();
        }
    }

    private void PruneViewers(ChannelBuffer buffer, DateTime now)
    {
        var idle = TimeSpan.FromSeconds(_opts.ViewerIdleSeconds);
        foreach (var stale in buffer.Viewers.Where(kv => now - kv.Value > idle).Select(kv => kv.Key).ToList())
            buffer.Viewers.Remove(stale);
    }
}
