using System.Diagnostics.Metrics;
using TailoredApps.KickGateway.Telemetry;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

// The metric names and tags below are what docs/MONITORING.md and the Grafana rules rely on.
[Collection("KickGatewayMetrics")]
public class KickGatewayMetricsTests
{
    private sealed record Sample(string Name, double Value, Dictionary<string, object?> Tags);

    private static (MeterListener Listener, List<Sample> Samples) Listen()
    {
        var samples = new List<Sample>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == KickGatewayMetrics.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(samples, i, v, tags));
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(samples, i, v, tags));
        listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Add(samples, i, v, tags));
        listener.Start();
        return (listener, samples);

        static void Add(List<Sample> samples, Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (samples) samples.Add(new Sample(i.Name, v, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        }
    }

    [Fact]
    public void Webhook_duration_is_seconds_tagged_with_event_type_and_outcome()
    {
        var (listener, samples) = Listen();
        using (listener)
        {
            KickGatewayMetrics.RecordWebhook("chat.message.sent", KickGatewayMetrics.Outcomes.Published, TimeSpan.FromMilliseconds(12));
            KickGatewayMetrics.RecordWebhook(null, KickGatewayMetrics.Outcomes.Rejected, TimeSpan.FromMilliseconds(1));
        }

        var published = Assert.Single(samples, s => s.Name == "kickgateway.webhook.duration" && Equals(s.Tags["outcome"], "published"));
        Assert.Equal(0.012, published.Value, 6);
        Assert.Equal("chat.message.sent", published.Tags["kick.event_type"]);

        var rejected = Assert.Single(samples, s => s.Name == "kickgateway.webhook.duration" && Equals(s.Tags["outcome"], "rejected"));
        Assert.Equal("unknown", rejected.Tags["kick.event_type"]); // no header on a rejected delivery
    }

    [Fact]
    public void Negative_delays_from_clock_skew_are_clamped_to_zero()
    {
        var (listener, samples) = Listen();
        using (listener)
        {
            KickGatewayMetrics.RecordWebhookKickDelay("chat.message.sent", TimeSpan.FromSeconds(-3));
            KickGatewayMetrics.RecordEventDeliveryLag("chat.message.sent", TimeSpan.FromMilliseconds(-5));
        }

        Assert.Equal(0, Assert.Single(samples, s => s.Name == "kickgateway.webhook.kick_delay").Value);
        Assert.Equal(0, Assert.Single(samples, s => s.Name == "kickgateway.event.delivery_lag").Value);
    }

    [Fact]
    public void Realtime_ingest_records_duration_drops_and_queue_depth()
    {
        var (listener, samples) = Listen();
        using (listener)
        {
            KickGatewayMetrics.RecordRealtimeIngest("ChatMessageEvent", KickGatewayMetrics.Outcomes.Duplicate, TimeSpan.FromMilliseconds(250));
            KickGatewayMetrics.RecordRealtimeDrop("queue_full");
            KickGatewayMetrics.SetRealtimeQueueDepthProvider(() => 7);
            try { listener.RecordObservableInstruments(); }
            finally { KickGatewayMetrics.SetRealtimeQueueDepthProvider(null); }
        }

        var duration = Assert.Single(samples, s => s.Name == "kickgateway.realtime.ingest.duration");
        Assert.Equal(0.25, duration.Value, 6);
        Assert.Equal("ChatMessageEvent", duration.Tags["kick.event"]);
        Assert.Equal("duplicate", duration.Tags["outcome"]);

        var drop = Assert.Single(samples, s => s.Name == "kickgateway.realtime.ingest.dropped");
        Assert.Equal(1, drop.Value);
        Assert.Equal("queue_full", drop.Tags["reason"]);

        Assert.Contains(samples, s => s.Name == "kickgateway.realtime.ingest.queue_depth" && s.Value == 7);
    }
}
