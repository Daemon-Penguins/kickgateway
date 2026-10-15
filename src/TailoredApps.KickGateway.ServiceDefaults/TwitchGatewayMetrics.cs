using System.Diagnostics.Metrics;

namespace TailoredApps.KickGateway.Telemetry;

/// <summary>
/// The Twitch side's own meter, the sibling of <see cref="KickGatewayMetrics"/>: EventSub frame -> inbox + outbox
/// commit, the queue in between, EventSub session health, the audio capture and the end-to-end delivery lag.
/// Names, units and tags are the contract with the dashboards and alert rules in <c>docs/MONITORING.md</c>.
/// Separate meter name so a Prometheus selector on <c>twitchgateway_*</c> isolates the platform.
/// </summary>
public static class TwitchGatewayMetrics
{
    public const string MeterName = "TailoredApps.TwitchGateway";

    public static readonly Meter Meter = new(MeterName);

    private static readonly double[] IngestBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];
    private static readonly double[] LagBuckets = [0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 120, 300];

    private static readonly Histogram<double> IngestDuration = Meter.CreateHistogram<double>(
        "twitchgateway.ingest.duration", "s",
        "From an EventSub notification (or polled Helix state) being received to its inbox row + outbox publish being committed (includes the wait in the ingest queue).",
        tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = IngestBuckets });

    private static readonly Counter<long> IngestDropped = Meter.CreateCounter<long>(
        "twitchgateway.ingest.dropped", "{event}",
        "Events dropped before ingest because the worker's queue was full.");

    private static readonly Counter<long> SessionEvents = Meter.CreateCounter<long>(
        "twitchgateway.eventsub.session.events", "{event}",
        "EventSub WebSocket session lifecycle: connected, keepalive_timeout, reconnect, closed, error, revoked, subscribe_failed, unauthorized.");

    private static readonly Counter<long> AudioSegments = Meter.CreateCounter<long>(
        "twitchgateway.audio.segments", "{segment}",
        "Live audio segments by outcome: published, ad_skipped, fetch_failed, too_large.");

    private static readonly Histogram<double> DeliveryLag = Meter.CreateHistogram<double>(
        "twitchgateway.event.delivery_lag", "s",
        "End to end: from the listener receiving a Twitch event to the published contract reaching a subscriber in the Api (outbox + RabbitMQ + consume).",
        tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LagBuckets });

    private static Func<int>? _queueDepth;
    private static Func<int>? _subscriptions;
    private static Func<int>? _liveCaptures;

    static TwitchGatewayMetrics()
    {
        Meter.CreateObservableGauge("twitchgateway.ingest.queue_depth", () => _queueDepth?.Invoke() ?? 0, "{event}",
            "Events waiting in the listener's ingest queue (all workers).");
        Meter.CreateObservableGauge("twitchgateway.eventsub.subscriptions", () => _subscriptions?.Invoke() ?? 0, "{subscription}",
            "Enabled EventSub subscriptions across the listener's sessions (0 while the bot token is missing or refused).");
        Meter.CreateObservableGauge("twitchgateway.audio.captures", () => _liveCaptures?.Invoke() ?? 0, "{channel}",
            "Channels whose live audio is being captured right now.");
    }

    public static void RecordIngest(string? eventType, string outcome, TimeSpan elapsed) =>
        IngestDuration.Record(Seconds(elapsed),
            new KeyValuePair<string, object?>("twitch.event", Tag(eventType)),
            new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordDrop(string reason) =>
        IngestDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public static void RecordSessionEvent(string reason) =>
        SessionEvents.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public static void RecordAudioSegment(string outcome) =>
        AudioSegments.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordDeliveryLag(string? eventType, TimeSpan lag) =>
        DeliveryLag.Record(Seconds(lag), new KeyValuePair<string, object?>("twitch.event", Tag(eventType)));

    public static void SetQueueDepthProvider(Func<int>? depth) => _queueDepth = depth;
    public static void SetSubscriptionsProvider(Func<int>? count) => _subscriptions = count;
    public static void SetLiveCapturesProvider(Func<int>? count) => _liveCaptures = count;

    private static double Seconds(TimeSpan t) => t < TimeSpan.Zero ? 0 : t.TotalSeconds;

    private static string Tag(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}
