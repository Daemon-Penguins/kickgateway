using System.Diagnostics.Metrics;

namespace TailoredApps.KickGateway.Telemetry;

/// <summary>
/// The solution's own OpenTelemetry meter. It measures the edges MassTransit cannot see: how long a
/// message from the outside world (a Kick webhook, a Pusher frame) takes to land in the inbox + outbox,
/// how late Kick delivers, and how long a published event takes to reach a subscriber. MassTransit's
/// built-in <c>MassTransit</c> meter (consume/receive duration, faults, retries, outbox delivery) and
/// ASP.NET Core's HTTP metrics sit next to it; <c>AddServiceDefaults()</c> exports all three.
/// <para>
/// Names, units and tags are the contract with the dashboards and alert rules in
/// <c>docs/MONITORING.md</c> - change both. Durations are seconds (Prometheus appends <c>_seconds</c>);
/// tag values are bounded (Kick's own event names plus a fixed outcome set).
/// </para>
/// </summary>
public static class KickGatewayMetrics
{
    public const string MeterName = "TailoredApps.KickGateway";

    public static readonly Meter Meter = new(MeterName);

    /// <summary>Values of the <c>outcome</c> tag on the ingest histograms.</summary>
    public static class Outcomes
    {
        /// <summary>Stored in the inbox and enlisted in the outbox - the normal case.</summary>
        public const string Published = "published";

        /// <summary>Already seen (Kick or Pusher redelivered it) - acknowledged without a publish.</summary>
        public const string Duplicate = "duplicate";

        /// <summary>Webhook for a broadcaster the gateway does not manage - recorded and dropped.</summary>
        public const string UnknownBroadcaster = "unknown_broadcaster";

        /// <summary>Webhook without the required Kick-Event-* headers (400).</summary>
        public const string Rejected = "rejected";

        /// <summary>Webhook whose signature did not verify (401).</summary>
        public const string Unauthorized = "unauthorized";

        /// <summary>The handler threw (webhook) or gave up after its retries (realtime) - the message is lost or will be redelivered.</summary>
        public const string Error = "error";
    }

    // Ingest is a few ms on a healthy DB, so the low end is fine-grained; the high end catches a stalled DB or broker.
    private static readonly double[] IngestBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];

    // Delays across systems: sub-second when healthy, tens of seconds when something backs up.
    private static readonly double[] LagBuckets = [0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 120, 300];

    private static readonly Histogram<double> WebhookDuration = Meter.CreateHistogram<double>(
        "kickgateway.webhook.duration", "s",
        "Time to handle one Kick webhook delivery: verify the signature, dedupe, store the inbox row and enlist the publish in the outbox.",
        tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = IngestBuckets });

    private static readonly Histogram<double> WebhookKickDelay = Meter.CreateHistogram<double>(
        "kickgateway.webhook.kick_delay", "s",
        "How late Kick delivered a webhook: from its Kick-Event-Message-Timestamp to the gateway receiving it (Kick's side, not ours).",
        tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LagBuckets });

    private static readonly Histogram<double> RealtimeIngestDuration = Meter.CreateHistogram<double>(
        "kickgateway.realtime.ingest.duration", "s",
        "From a Pusher frame being received to its inbox row + outbox publish being committed (includes the wait in the ingest queue).",
        tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = IngestBuckets });

    private static readonly Counter<long> RealtimeIngestDropped = Meter.CreateCounter<long>(
        "kickgateway.realtime.ingest.dropped", "{frame}",
        "Pusher frames dropped before ingest because the worker's queue was full.");

    private static readonly Counter<long> RealtimeConnectionErrors = Meter.CreateCounter<long>(
        "kickgateway.realtime.connection.errors", "{session}",
        "Pusher sessions that ended abnormally: refused (4000-4099: the app key / cluster is wrong - Kick rotated it), pusher_error (other pusher:error codes), dropped (socket error) or closed (by the server).");

    private static readonly Histogram<double> EventDeliveryLag = Meter.CreateHistogram<double>(
        "kickgateway.event.delivery_lag", "s",
        "End to end: from a Kick webhook reaching the gateway to the published event reaching a subscriber in the Api (outbox + RabbitMQ + consume).",
        tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LagBuckets });

    private static Func<int>? _realtimeQueueDepth;

    static KickGatewayMetrics()
    {
        Meter.CreateObservableGauge("kickgateway.realtime.ingest.queue_depth",
            () => _realtimeQueueDepth?.Invoke() ?? 0, "{frame}",
            "Pusher frames waiting in the realtime listener's ingest queue (all workers).");
    }

    public static void RecordWebhook(string? eventType, string outcome, TimeSpan elapsed) =>
        WebhookDuration.Record(Seconds(elapsed),
            new KeyValuePair<string, object?>("kick.event_type", Tag(eventType)),
            new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordWebhookKickDelay(string? eventType, TimeSpan delay) =>
        WebhookKickDelay.Record(Seconds(delay), new KeyValuePair<string, object?>("kick.event_type", Tag(eventType)));

    /// <param name="pusherEvent">Short Pusher event name (<c>ChatMessageEvent</c>), not the namespaced one.</param>
    public static void RecordRealtimeIngest(string? pusherEvent, string outcome, TimeSpan elapsed) =>
        RealtimeIngestDuration.Record(Seconds(elapsed),
            new KeyValuePair<string, object?>("kick.event", Tag(pusherEvent)),
            new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordRealtimeDrop(string reason) =>
        RealtimeIngestDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <param name="reason"><c>refused</c>, <c>pusher_error</c>, <c>dropped</c> or <c>closed</c>.</param>
    /// <param name="pusherCode">The <c>pusher:error</c> code (4001 ...), 0 when there was none.</param>
    public static void RecordRealtimeConnectionError(string reason, int pusherCode) =>
        RealtimeConnectionErrors.Add(1,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("pusher.code", pusherCode));

    public static void RecordEventDeliveryLag(string? eventType, TimeSpan lag) =>
        EventDeliveryLag.Record(Seconds(lag), new KeyValuePair<string, object?>("kick.event_type", Tag(eventType)));

    /// <summary>Registers the realtime ingest queue's depth (one queue per process; the last registration wins, null unregisters).</summary>
    public static void SetRealtimeQueueDepthProvider(Func<int>? depth) => _realtimeQueueDepth = depth;

    // Clock skew (Kick vs. us, or between two of our hosts) can make a delay negative: clamp, never drop the sample.
    private static double Seconds(TimeSpan t) => t < TimeSpan.Zero ? 0 : t.TotalSeconds;

    private static string Tag(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}
