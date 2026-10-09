using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Consumers;

/// <summary>Hands each received live-video segment to the per-channel session (decoder + chunker).</summary>
public sealed class LiveVideoSegmentConsumer : IConsumer<LiveVideoSegment>
{
    private readonly TranscriptionCoordinator _coordinator;

    public LiveVideoSegmentConsumer(TranscriptionCoordinator coordinator) => _coordinator = coordinator;

    public Task Consume(ConsumeContext<LiveVideoSegment> context) =>
        _coordinator.HandleSegmentAsync(context.Message, context.CancellationToken);
}
