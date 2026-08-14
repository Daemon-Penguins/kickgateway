using System.Collections.Concurrent;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subscribers.VideoRecorder;

/// <summary>Where the recorder writes reassembled files.</summary>
public sealed record RecorderOptions(string OutputDir);

/// <summary>
/// Reassembles <see cref="LiveVideoSegment"/> messages into playable files, one per channel per
/// stream session — the end-to-end proof that the video pipeline works: segments arrive AND
/// reconstruct into valid media.
///
/// Two container shapes are handled:
/// <list type="bullet">
/// <item><b>fMP4 / CMAF</b> (<c>video/mp4</c>): the <see cref="LiveVideoSegment.IsInitSegment"/>
/// (<c>EXT-X-MAP</c>) bytes are written first, then media fragments are appended → a playable
/// fragmented <c>.mp4</c>. A new init rolls a fresh file.</item>
/// <item><b>MPEG-TS</b> (<c>video/mp2t</c>): there is no init segment; <c>.ts</c> segments are
/// simply concatenated in order → a playable <c>.ts</c>.</item>
/// </list>
/// Files are flushed after every write, so even if the process is killed mid-stream the file on
/// disk is already valid up to the last segment. Writes are serialized (the recorder's receive
/// endpoint runs single-threaded) so segments land in publish order.
/// </summary>
public sealed class VideoFileAssembler : IDisposable
{
    private readonly string _dir;
    private readonly ILogger<VideoFileAssembler> _log;
    private readonly ConcurrentDictionary<string, ChannelState> _channels = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VideoFileAssembler(RecorderOptions opts, ILogger<VideoFileAssembler> log)
    {
        _dir = opts.OutputDir;
        _log = log;
    }

    public async Task WriteAsync(LiveVideoSegment seg, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var slug = string.IsNullOrWhiteSpace(seg.BroadcasterSlug) ? "unknown" : seg.BroadcasterSlug;
            var st = _channels.GetOrAdd(slug, _ => new ChannelState());

            if (seg.IsInitSegment)
            {
                // The producer re-emits the init periodically so mid-stream joiners can bootstrap.
                // If we're already recording with this exact init, ignore the repeat — otherwise we'd
                // start a new file every few seconds.
                if (st.Stream is not null && st.InitBytes is not null && st.InitBytes.AsSpan().SequenceEqual(seg.Data))
                    return;

                // First init, or a genuinely changed one (new codec/session) → begin a fresh,
                // independently-playable file with this init written first.
                await CloseAsync(st);
                st.IsFmp4 = true;
                st.InitBytes = seg.Data;
                OpenNewFile(st, slug, "mp4");
                await AppendAsync(st, seg.Data, ct);
                st.LastSeq = -1;
                _log.LogInformation("[{Slug}] init segment ({Bytes} B) → new file {File}", slug, seg.Data.Length, Path.GetFileName(st.Path));
                return;
            }

            if (st.Stream is null)
            {
                if (IsTransportStream(seg))
                {
                    // TS needs no init — start concatenating.
                    st.IsFmp4 = false;
                    OpenNewFile(st, slug, "ts");
                }
                else
                {
                    // fMP4 media before we've seen its init — can't produce a valid file yet.
                    if (!st.WarnedNoInit)
                    {
                        _log.LogWarning("[{Slug}] fMP4 media segment arrived before its init (EXT-X-MAP) — waiting for the init segment", slug);
                        st.WarnedNoInit = true;
                    }
                    return;
                }
            }

            if (seg.MediaSequence <= st.LastSeq) return; // duplicate / older redelivery

            await AppendAsync(st, seg.Data, ct);
            st.LastSeq = seg.MediaSequence;
            st.SegmentCount++;

            if (st.SegmentCount == 1 || st.SegmentCount % 10 == 0)
                _log.LogInformation("[{Slug}] {Count} segment(s), {Kb} KB → {File}  (open in VLC/ffplay to verify)",
                    slug, st.SegmentCount, st.Bytes / 1024, st.Path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OpenNewFile(ChannelState st, string slug, string ext)
    {
        var name = $"{Sanitize(slug)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{ext}";
        st.Path = Path.Combine(_dir, name);
        st.Stream = new FileStream(st.Path, FileMode.Create, FileAccess.Write, FileShare.Read);
        st.Bytes = 0;
        st.SegmentCount = 0;
        st.WarnedNoInit = false;
    }

    private static async Task AppendAsync(ChannelState st, byte[] data, CancellationToken ct)
    {
        if (st.Stream is null || data.Length == 0) return;
        await st.Stream.WriteAsync(data, ct);
        await st.Stream.FlushAsync(ct); // keep the on-disk file valid after every segment
        st.Bytes += data.Length;
    }

    private static async Task CloseAsync(ChannelState st)
    {
        if (st.Stream is null) return;
        await st.Stream.FlushAsync();
        await st.Stream.DisposeAsync();
        st.Stream = null;
    }

    /// <summary>TS vs fMP4 — by MIME first, then the source URL extension as a fallback.</summary>
    public static bool IsTransportStream(LiveVideoSegment seg)
    {
        if (seg.ContentType.Contains("mp2t", StringComparison.OrdinalIgnoreCase)) return true;
        if (seg.ContentType.Contains("mp4", StringComparison.OrdinalIgnoreCase)) return false;
        return seg.SegmentUri.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string slug) =>
        new string(slug.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    public void Dispose()
    {
        foreach (var st in _channels.Values)
        {
            try { st.Stream?.Flush(); st.Stream?.Dispose(); } catch { /* best-effort on shutdown */ }
        }
        _gate.Dispose();
    }

    private sealed class ChannelState
    {
        public FileStream? Stream;
        public string Path = "";
        public long LastSeq = -1;
        public long Bytes;
        public int SegmentCount;
        public bool IsFmp4;
        public bool WarnedNoInit;
        public byte[]? InitBytes; // current fMP4 init — used to detect re-emitted duplicates
    }
}
