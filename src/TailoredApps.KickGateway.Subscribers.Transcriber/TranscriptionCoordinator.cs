using System.Collections.Concurrent;
using System.Threading.Channels;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subscribers.Transcriber;

/// <summary>
/// Owns the per-channel <see cref="ChannelSession"/>s and the bounded queue of chunks waiting for
/// Whisper. Segments come in from the MassTransit consumer; a 1 s sweep flushes idle channels and
/// disposes expired ones. The queue drops the <i>oldest</i> chunk when full: when Whisper can't keep
/// up we prefer to stay live with gaps over falling further and further behind.
/// </summary>
public sealed class TranscriptionCoordinator : BackgroundService
{
    private static readonly TimeSpan DropLogInterval = TimeSpan.FromSeconds(10);

    private readonly TranscriberOptions _opts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TranscriptionCoordinator> _log;
    private readonly ConcurrentDictionary<string, ChannelSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<TranscriptionJob> _jobs;

    private long _dropped;
    private long _droppedSinceLog;
    private DateTime _lastDropLog = DateTime.MinValue;

    public TranscriptionCoordinator(TranscriberOptions opts, ILoggerFactory loggerFactory)
    {
        _opts = opts;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<TranscriptionCoordinator>();
        _jobs = Channel.CreateBounded<TranscriptionJob>(
            new BoundedChannelOptions(Math.Max(1, opts.MaxPendingChunks))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            OnJobDropped);
    }

    /// <summary>Chunks for the Whisper worker, oldest first.</summary>
    public ChannelReader<TranscriptionJob> Jobs => _jobs.Reader;

    public int ActiveSessions => _sessions.Count;

    public long DroppedChunks => Interlocked.Read(ref _dropped);

    /// <summary>Routes a segment to its channel's session, creating the session on first contact.</summary>
    public Task HandleSegmentAsync(LiveVideoSegment seg, CancellationToken ct)
    {
        var slug = string.IsNullOrWhiteSpace(seg.BroadcasterSlug) ? "unknown" : seg.BroadcasterSlug.ToLowerInvariant();
        var session = _sessions.GetOrAdd(slug, s =>
        {
            _log.LogInformation("[{Slug}] new transcription session", s);
            return new ChannelSession(s, _opts, Enqueue, _loggerFactory.CreateLogger<ChannelSession>());
        });
        return session.FeedAsync(seg, ct);
    }

    private void Enqueue(TranscriptionJob job)
    {
        // Never blocks: DropOldest evicts the head when full (see OnJobDropped).
        _jobs.Writer.TryWrite(job);
    }

    private void OnJobDropped(TranscriptionJob job)
    {
        Interlocked.Increment(ref _dropped);
        var pending = Interlocked.Increment(ref _droppedSinceLog);
        var now = DateTime.UtcNow;
        if (now - _lastDropLog < DropLogInterval) return;
        _lastDropLog = now;
        Interlocked.Exchange(ref _droppedSinceLog, 0);
        _log.LogWarning("Whisper is falling behind — dropped {Count} pending chunk(s) in the last {Sec}s (latest: [{Slug}] {Len:F1}s). " +
                        "Use a smaller model, enable the GPU, or transcribe fewer channels.",
            pending, (int)DropLogInterval.TotalSeconds, job.Slug, job.Seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                foreach (var (slug, session) in _sessions)
                {
                    if (session.IsExpired(now))
                    {
                        if (_sessions.TryRemove(slug, out var removed))
                        {
                            _log.LogInformation("[{Slug}] no segments for {Sec:F0}s — closing session", slug, (now - removed.LastSegmentAt).TotalSeconds);
                            await removed.DisposeAsync();
                        }
                        continue;
                    }

                    session.FlushIfIdle(now);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }

        foreach (var slug in _sessions.Keys.ToArray())
            if (_sessions.TryRemove(slug, out var session))
                await session.DisposeAsync();

        _jobs.Writer.TryComplete();
    }
}
