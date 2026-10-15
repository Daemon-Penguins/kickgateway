using System.Threading.Channels;
using TailoredApps.KickGateway.Telemetry;
using TailoredApps.TwitchGateway.Contracts;

namespace TailoredApps.TwitchGateway.Listener.Ingest;

public enum TwitchIngestOutcome
{
    Published,
    Duplicate,
}

/// <summary>One mapped event waiting for an ingest worker. <see cref="PartitionKey"/> keeps a channel's events ordered.</summary>
public sealed record PendingTwitchEvent(string PartitionKey, ITwitchEvent Event, DateTime ReceivedAt);

/// <summary>
/// Decouples the EventSub receive loops (and the Helix poller) from the database: a receive loop must
/// keep draining frames and answering pings, so it only hands each mapped event to this queue and never
/// awaits a DB round trip. Events are hashed by broadcaster onto a fixed set of partitions with one worker
/// each: one channel's chat stays in order while channels commit in parallel. Partitions are bounded -
/// when one is full the newest event is dropped and counted (<c>twitchgateway.ingest.dropped</c>). A
/// failed commit is retried a few times (transient SQL errors) before the event is logged and dropped.
/// The Kick listener has the same queue for Pusher frames; this one is kept separate on purpose.
/// </summary>
public sealed class TwitchIngestQueue : IAsyncDisposable
{
    public delegate Task<TwitchIngestOutcome> IngestHandler(ITwitchEvent evt, CancellationToken ct);

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly Channel<PendingTwitchEvent>[] _partitions;
    private readonly IngestHandler _handler;
    private readonly ILogger _log;
    private readonly int _retries;
    private readonly TimeSpan _retryDelay;
    private readonly CancellationTokenSource _cts = new();
    private Task[] _workers = [];
    private long _dropped;
    private long _lastDropWarningTicks = long.MinValue / 2;

    public TwitchIngestQueue(int workers, int capacityPerWorker, int retries, TimeSpan retryDelay, IngestHandler handler, ILogger log)
    {
        workers = Math.Clamp(workers, 1, 64);
        Capacity = Math.Clamp(capacityPerWorker, 1, 1_000_000);
        _retries = Math.Clamp(retries, 0, 10);
        _retryDelay = retryDelay < TimeSpan.Zero ? TimeSpan.Zero : retryDelay;
        _handler = handler;
        _log = log;

        _partitions = new Channel<PendingTwitchEvent>[workers];
        for (var i = 0; i < workers; i++)
            _partitions[i] = Channel.CreateBounded<PendingTwitchEvent>(new BoundedChannelOptions(Capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        TwitchGatewayMetrics.SetQueueDepthProvider(() => Depth);
    }

    public int Workers => _partitions.Length;
    public int Capacity { get; }

    public int Depth
    {
        get
        {
            var n = 0;
            foreach (var p in _partitions) n += p.Reader.Count;
            return n;
        }
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Start()
    {
        if (_workers.Length > 0) return;
        _workers = new Task[_partitions.Length];
        for (var i = 0; i < _partitions.Length; i++)
        {
            var reader = _partitions[i].Reader;
            _workers[i] = Task.Run(() => RunWorkerAsync(reader, _cts.Token));
        }
    }

    /// <summary>Queues an event. False (and a counted drop) when its partition is full.</summary>
    public bool TryEnqueue(PendingTwitchEvent item)
    {
        var partition = _partitions[PartitionOf(item.PartitionKey)];
        if (partition.Writer.TryWrite(item)) return true;

        var dropped = Interlocked.Increment(ref _dropped);
        TwitchGatewayMetrics.RecordDrop("queue_full");
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastDropWarningTicks);
        if (now - last >= 10_000 && Interlocked.CompareExchange(ref _lastDropWarningTicks, now, last) == last)
            _log.LogWarning("Twitch ingest queue full ({Capacity} per worker) - dropping {Event} on {Channel}; {Dropped} dropped so far",
                Capacity, item.Event.EventType, item.Event.BroadcasterLogin, dropped);
        return false;
    }

    public int PartitionOf(string? key)
    {
        if (_partitions.Length == 1 || string.IsNullOrEmpty(key)) return 0;
        return (key.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % _partitions.Length;
    }

    private async Task RunWorkerAsync(ChannelReader<PendingTwitchEvent> reader, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct))
                while (reader.TryRead(out var item))
                    await ProcessAsync(item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Twitch ingest worker stopped unexpectedly");
        }
    }

    private async Task ProcessAsync(PendingTwitchEvent item, CancellationToken ct)
    {
        var type = item.Event.EventType;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var outcome = await _handler(item.Event, ct);
                TwitchGatewayMetrics.RecordIngest(type,
                    outcome == TwitchIngestOutcome.Duplicate ? KickGatewayMetrics.Outcomes.Duplicate : KickGatewayMetrics.Outcomes.Published,
                    DateTime.UtcNow - item.ReceivedAt);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < _retries)
            {
                _log.LogDebug(ex, "Ingest of {Event} on {Channel} failed (attempt {Attempt}/{Max}) - retrying", type, item.Event.BroadcasterLogin, attempt + 1, _retries + 1);
                await Task.Delay(_retryDelay * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                TwitchGatewayMetrics.RecordIngest(type, KickGatewayMetrics.Outcomes.Error, DateTime.UtcNow - item.ReceivedAt);
                _log.LogWarning(ex, "Failed to ingest Twitch event {Event} on {Channel} after {Attempts} attempt(s) - event lost", type, item.Event.BroadcasterLogin, attempt + 1);
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _partitions) p.Writer.TryComplete();
        if (_workers.Length > 0)
        {
            var drained = Task.WhenAll(_workers);
            if (await Task.WhenAny(drained, Task.Delay(DrainTimeout)) != drained)
                _log.LogWarning("Twitch ingest queue did not drain within {Timeout}s - {Depth} event(s) abandoned", DrainTimeout.TotalSeconds, Depth);
            _cts.Cancel();
            await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        else
        {
            _cts.Cancel();
        }
        _cts.Dispose();
        TwitchGatewayMetrics.SetQueueDepthProvider(null);
    }
}
