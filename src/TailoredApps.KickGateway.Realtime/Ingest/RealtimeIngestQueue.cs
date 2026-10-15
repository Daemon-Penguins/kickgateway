using System.Threading.Channels;
using TailoredApps.KickGateway.Realtime.Mapping;
using TailoredApps.KickGateway.Realtime.Pusher;
using TailoredApps.KickGateway.Telemetry;

namespace TailoredApps.KickGateway.Realtime.Ingest;

/// <summary>
/// Decouples the Pusher receive loops from the database. A WebSocket receive loop has to keep draining
/// frames and answering pings, so it only hands each app-event frame to this queue and never awaits a DB
/// round trip (before this, one slow commit stalled every channel on that connection, and a long enough
/// stall meant a reconnect and lost frames). Frames are hashed by Pusher channel onto a fixed set of
/// partitions with one worker each: frames of one chatroom stay in order while different channels commit
/// in parallel. Partitions are bounded - when one is full the newest frame is dropped and counted
/// (<c>kickgateway.realtime.ingest.dropped</c>) rather than stalling the socket. A frame whose commit fails
/// is retried a few times (transient SQL errors) before it is logged and dropped.
/// </summary>
public sealed class RealtimeIngestQueue : IAsyncDisposable
{
    /// <summary>Maps and records one frame; <paramref name="receivedAt"/> is when the listener took it off the socket.</summary>
    public delegate Task<IngestOutcome> IngestHandler(PusherFrame frame, ManagedChannel channel, DateTime receivedAt, CancellationToken ct);

    private readonly record struct PendingFrame(PusherFrame Frame, ManagedChannel Channel, DateTime ReceivedAt);

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly Channel<PendingFrame>[] _partitions;
    private readonly IngestHandler _handler;
    private readonly ILogger _log;
    private readonly int _retries;
    private readonly TimeSpan _retryDelay;
    private readonly CancellationTokenSource _cts = new();
    private Task[] _workers = [];
    private long _dropped;
    private long _lastDropWarningTicks = long.MinValue / 2;

    public RealtimeIngestQueue(int workers, int capacityPerWorker, int retries, TimeSpan retryDelay, IngestHandler handler, ILogger log)
    {
        workers = Math.Clamp(workers, 1, 64);
        Capacity = Math.Clamp(capacityPerWorker, 1, 1_000_000);
        _retries = Math.Clamp(retries, 0, 10);
        _retryDelay = retryDelay < TimeSpan.Zero ? TimeSpan.Zero : retryDelay;
        _handler = handler;
        _log = log;

        _partitions = new Channel<PendingFrame>[workers];
        for (var i = 0; i < workers; i++)
            _partitions[i] = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(Capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                // Wait + TryWrite = "false when full": the caller drops and counts instead of blocking the socket.
                FullMode = BoundedChannelFullMode.Wait,
            });

        KickGatewayMetrics.SetRealtimeQueueDepthProvider(() => Depth);
    }

    public int Workers => _partitions.Length;

    /// <summary>Frames buffered per worker before the newest is dropped.</summary>
    public int Capacity { get; }

    /// <summary>Frames waiting across all partitions (the <c>kickgateway.realtime.ingest.queue_depth</c> gauge).</summary>
    public int Depth
    {
        get
        {
            var n = 0;
            foreach (var p in _partitions) n += p.Reader.Count;
            return n;
        }
    }

    /// <summary>Frames dropped because their partition was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Starts one worker per partition. Frames enqueued before this simply wait.</summary>
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

    /// <summary>Queues a frame for ingest. False (and a counted drop) when its partition is full.</summary>
    public bool TryEnqueue(PusherFrame frame, ManagedChannel channel, DateTime receivedAt)
    {
        var partition = _partitions[PartitionOf(frame.Channel)];
        if (partition.Writer.TryWrite(new PendingFrame(frame, channel, receivedAt))) return true;

        var dropped = Interlocked.Increment(ref _dropped);
        KickGatewayMetrics.RecordRealtimeDrop("queue_full");

        // One warning per 10 s is enough: the metric carries the count.
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastDropWarningTicks);
        if (now - last >= 10_000 && Interlocked.CompareExchange(ref _lastDropWarningTicks, now, last) == last)
            _log.LogWarning("Realtime ingest queue full ({Capacity} per worker) - dropping {Event} on {Channel}; {Dropped} dropped so far",
                Capacity, RealtimeFrameMapper.ShortName(frame.Event), frame.Channel, dropped);
        return false;
    }

    /// <summary>Partition index for a Pusher channel - stable within the process, so one channel always hits one worker.</summary>
    public int PartitionOf(string? channel)
    {
        if (_partitions.Length == 1 || string.IsNullOrEmpty(channel)) return 0;
        return (channel.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % _partitions.Length;
    }

    private async Task RunWorkerAsync(ChannelReader<PendingFrame> reader, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct))
                while (reader.TryRead(out var item))
                    await ProcessAsync(item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown past the drain timeout
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Realtime ingest worker stopped unexpectedly");
        }
    }

    private async Task ProcessAsync(PendingFrame item, CancellationToken ct)
    {
        var name = RealtimeFrameMapper.ShortName(item.Frame.Event);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var outcome = await _handler(item.Frame, item.Channel, item.ReceivedAt, ct);
                KickGatewayMetrics.RecordRealtimeIngest(name,
                    outcome == IngestOutcome.Duplicate ? KickGatewayMetrics.Outcomes.Duplicate : KickGatewayMetrics.Outcomes.Published,
                    DateTime.UtcNow - item.ReceivedAt);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < _retries)
            {
                _log.LogDebug(ex, "Ingest of {Event} on {Channel} failed (attempt {Attempt}/{Max}) - retrying",
                    name, item.Frame.Channel, attempt + 1, _retries + 1);
                await Task.Delay(_retryDelay * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                KickGatewayMetrics.RecordRealtimeIngest(name, KickGatewayMetrics.Outcomes.Error, DateTime.UtcNow - item.ReceivedAt);
                _log.LogWarning(ex, "Failed to ingest realtime frame {Event} on {Channel} after {Attempts} attempt(s) - frame lost",
                    name, item.Frame.Channel, attempt + 1);
                return;
            }
        }
    }

    /// <summary>Stops accepting frames, lets the workers drain what is queued (bounded wait), then cancels them.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var p in _partitions) p.Writer.TryComplete();

        if (_workers.Length > 0)
        {
            var drained = Task.WhenAll(_workers);
            if (await Task.WhenAny(drained, Task.Delay(DrainTimeout)) != drained)
                _log.LogWarning("Realtime ingest queue did not drain within {Timeout}s - {Depth} frame(s) abandoned",
                    DrainTimeout.TotalSeconds, Depth);
            _cts.Cancel();
            await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        else
        {
            _cts.Cancel();
        }

        _cts.Dispose();
        KickGatewayMetrics.SetRealtimeQueueDepthProvider(null);
    }
}
