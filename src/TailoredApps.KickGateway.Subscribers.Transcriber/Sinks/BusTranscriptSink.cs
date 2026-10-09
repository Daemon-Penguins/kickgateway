using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Sinks;

/// <summary>
/// Publishes each transcript as <see cref="LiveTranscript"/> on its slug-routed topic exchange
/// (see <see cref="KickMediaTopology"/>). Direct <see cref="IBus"/> publish — this service has no
/// database and therefore no outbox; transcripts are small and carry no TTL.
/// </summary>
public sealed class BusTranscriptSink : ITranscriptSink
{
    private readonly IBus _bus;

    public BusTranscriptSink(IBus bus) => _bus = bus;

    public string Name => "bus";

    public Task WriteAsync(LiveTranscript transcript, CancellationToken ct) => _bus.Publish(transcript, ct);
}
