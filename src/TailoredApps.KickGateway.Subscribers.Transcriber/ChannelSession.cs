using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.Transcriber.Audio;

namespace TailoredApps.KickGateway.Subscribers.Transcriber;

/// <summary>
/// Everything the transcriber keeps per channel: the ffmpeg decoder, the chunker that cuts its PCM
/// into Whisper-sized pieces, and the segment timeline that turns chunk positions back into stream
/// time. Created on a channel's first segment, torn down by the coordinator after
/// <see cref="TranscriberOptions.SessionTimeoutSeconds"/> without segments.
/// <para>
/// Two locks: <c>_gate</c> serializes decoder lifecycle + feeding (consumer thread vs. coordinator
/// sweep); <c>_audioSync</c> protects the chunker/timeline, which the decoder's stdout pump thread
/// writes into while the consumer thread records segments.
/// </para>
/// </summary>
public sealed class ChannelSession : IAsyncDisposable
{
    /// <summary>A forced flush (session teardown) still skips tails shorter than this — not worth a Whisper call.</summary>
    private const double MinForcedFlushSeconds = 1.0;

    /// <summary>fMP4 media segments kept while waiting for the init (the producer re-emits it every ~10 s); replayed once it lands.</summary>
    private const int MaxPreInitSegments = 16;

    /// <summary>A media sequence that jumps back by at least this much is a new stream session (producer reconnect), not a redelivery.</summary>
    private const long SequenceResetGap = 50;

    private readonly string _slug;
    private readonly TranscriberOptions _opts;
    private readonly Action<TranscriptionJob> _enqueue;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _audioSync = new();
    private readonly AudioChunker _chunker;
    private readonly SegmentTimeline _timeline = new();
    private readonly List<AudioChunk> _scratch = new();
    private readonly Queue<LiveVideoSegment> _preInit = new();

    private AudioDecoder? _decoder;
    private byte[]? _initBytes;
    private bool _isFmp4;
    private bool _warnedNoInit;
    private long _lastSeq = -1;
    private string _channelId = "";
    private long _fedSegments, _emittedChunks, _silentChunks, _restarts;
    private bool _disposed;

    public ChannelSession(string slug, TranscriberOptions opts, Action<TranscriptionJob> enqueue, ILogger log)
    {
        _slug = slug;
        _opts = opts;
        _enqueue = enqueue;
        _log = log;
        _chunker = new AudioChunker(AudioDecoder.SampleRate, opts.ChunkSeconds, opts.MinChunkSeconds);
        LastSegmentAt = LastSamplesAt = DateTime.UtcNow;
    }

    public string Slug => _slug;

    /// <summary>When the last segment arrived (any kind). Drives session expiry.</summary>
    public DateTime LastSegmentAt { get; private set; }

    /// <summary>When ffmpeg last produced samples. Drives the idle flush.</summary>
    public DateTime LastSamplesAt { get; private set; }

    public bool IsExpired(DateTime now) => (now - LastSegmentAt).TotalSeconds >= _opts.SessionTimeoutSeconds;

    /// <summary>Feeds one segment to the decoder, starting or restarting ffmpeg as the container demands.</summary>
    public async Task FeedAsync(LiveVideoSegment seg, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            LastSegmentAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(seg.KickChannelId)) _channelId = seg.KickChannelId;

            if (seg.IsInitSegment)
            {
                // The producer re-emits the init every few seconds so mid-stream joiners can bootstrap.
                if (_decoder is { HasExited: false } && _initBytes is not null && _initBytes.AsSpan().SequenceEqual(seg.Data))
                    return;

                _isFmp4 = true;
                _initBytes = seg.Data;
                await RestartDecoderAsync(_decoder is null ? "first segment (fMP4)" : "new init segment", ct);
                _lastSeq = -1; // a new init means a new stream session — sequence numbers may restart

                // Anything that arrived before the init is still valid media for it — replay in order.
                if (_preInit.Count > 0)
                {
                    _log.LogInformation("[{Slug}] init segment arrived — replaying {Count} buffered media segment(s)", _slug, _preInit.Count);
                    while (_preInit.TryDequeue(out var buffered))
                        await FeedMediaAsync(buffered, ct);
                }
                return;
            }

            if (_decoder is null || _decoder.HasExited)
            {
                var container = seg.DetectContainer();
                if (container == MediaContainer.Fmp4 && _initBytes is null)
                {
                    // Can't decode fMP4 without its init; keep a short tail so nothing is lost when it lands.
                    if (_preInit.Count >= MaxPreInitSegments) _preInit.Dequeue();
                    _preInit.Enqueue(seg);
                    if (!_warnedNoInit)
                    {
                        _log.LogWarning("[{Slug}] fMP4 media segment arrived before its init (EXT-X-MAP) — buffering up to {Max} segments until it arrives", _slug, MaxPreInitSegments);
                        _warnedNoInit = true;
                    }
                    return;
                }
                _isFmp4 = container == MediaContainer.Fmp4;
                await RestartDecoderAsync(
                    _decoder is null ? $"first segment ({Describe(container)})" : "ffmpeg exited", ct);
            }

            await FeedMediaAsync(seg, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller holds <c>_gate</c> and the decoder is running.</summary>
    private async Task FeedMediaAsync(LiveVideoSegment seg, CancellationToken ct)
    {
        if (seg.MediaSequence <= _lastSeq)
        {
            if (_lastSeq - seg.MediaSequence < SequenceResetGap) return; // duplicate / older redelivery
            _log.LogInformation("[{Slug}] media sequence went back from {Old} to {New} — producer reconnected, treating as a new stream session",
                _slug, _lastSeq, seg.MediaSequence);
        }

        lock (_audioSync) _timeline.Add(seg.MediaSequence, seg.Duration, seg.CapturedAt);

        try
        {
            await _decoder!.WriteAsync(seg.Data, ct);
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "[{Slug}] ffmpeg stopped accepting input — restarting the decoder on the next segment", _slug);
            await StopDecoderAsync();
        }

        _lastSeq = seg.MediaSequence;
        _fedSegments++;
    }

    private static string Describe(MediaContainer c) => c switch
    {
        MediaContainer.TransportStream => "MPEG-TS",
        MediaContainer.Fmp4 => "fMP4",
        _ => "unknown container, letting ffmpeg probe",
    };

    /// <summary>Emits the buffered tail once the stream has been silent for <see cref="TranscriberOptions.IdleFlushSeconds"/>.</summary>
    public void FlushIfIdle(DateTime now)
    {
        lock (_audioSync)
        {
            if (_chunker.Buffered == 0) return;
            if ((now - LastSamplesAt).TotalSeconds < _opts.IdleFlushSeconds) return;
            var chunk = _chunker.Flush(force: false);
            if (chunk is not null) EmitLocked(chunk);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopDecoderAsync();
            lock (_audioSync)
            {
                var chunk = _chunker.Flush(force: true);
                if (chunk is not null && chunk.Seconds(AudioDecoder.SampleRate) >= MinForcedFlushSeconds) EmitLocked(chunk);
            }
            _log.LogInformation("[{Slug}] session closed — {Segments} segment(s) decoded, {Chunks} chunk(s) sent to Whisper, {Silent} silent, {Restarts} decoder restart(s)",
                _slug, _fedSegments, _emittedChunks, _silentChunks, _restarts);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- internals (caller holds _gate) ----

    private async Task RestartDecoderAsync(string reason, CancellationToken ct)
    {
        if (_decoder is not null)
        {
            _restarts++;
            _log.LogInformation("[{Slug}] restarting ffmpeg: {Reason}", _slug, reason);
            await StopDecoderAsync();
        }
        else
        {
            _log.LogInformation("[{Slug}] starting ffmpeg: {Reason}", _slug, reason);
        }

        lock (_audioSync)
        {
            // Whatever ffmpeg had left undecoded is gone with it: emit what we have and snap the
            // audio clock back onto the fed timeline so timestamps don't drift by the lost tail.
            var chunk = _chunker.Flush(force: false);
            if (chunk is not null) EmitLocked(chunk);
            _chunker.Reset((long)(_timeline.FedSeconds * AudioDecoder.SampleRate));
        }

        _decoder = new AudioDecoder(_opts.FfmpegPath, _slug, OnSamples, _log);
        if (_isFmp4 && _initBytes is not null)
            await _decoder.WriteAsync(_initBytes, ct);
    }

    private async Task StopDecoderAsync()
    {
        var d = _decoder;
        _decoder = null;
        if (d is null) return;
        await d.DisposeAsync(); // closes stdin → ffmpeg drains → the pump delivers the last samples
    }

    /// <summary>Called on the decoder's stdout pump thread with each batch of decoded PCM.</summary>
    private void OnSamples(float[] samples)
    {
        lock (_audioSync)
        {
            LastSamplesAt = DateTime.UtcNow;
            _scratch.Clear();
            _chunker.Append(samples, _scratch);
            foreach (var chunk in _scratch) EmitLocked(chunk);
            _scratch.Clear();
        }
    }

    /// <summary>Caller holds <c>_audioSync</c>.</summary>
    private void EmitLocked(AudioChunk chunk)
    {
        var seconds = chunk.Seconds(AudioDecoder.SampleRate);
        var rms = chunk.Rms();
        if (rms < _opts.SilenceRms)
        {
            _silentChunks++;
            _log.LogDebug("[{Slug}] {Sec:F1}s chunk is silence (rms {Rms:F4}) — skipped", _slug, seconds, rms);
            return;
        }

        var startSeconds = chunk.StartSample / (double)AudioDecoder.SampleRate;
        var endSeconds = chunk.EndSample / (double)AudioDecoder.SampleRate;
        var (startedAt, firstSeq) = _timeline.Resolve(startSeconds);
        var (_, lastSeq) = _timeline.Resolve(Math.Max(startSeconds, endSeconds - 0.001));

        _emittedChunks++;
        _enqueue(new TranscriptionJob(
            _slug, _channelId, chunk.Samples, startSeconds,
            startedAt, startedAt.AddSeconds(seconds),
            firstSeq, lastSeq, DateTime.UtcNow));
    }
}
