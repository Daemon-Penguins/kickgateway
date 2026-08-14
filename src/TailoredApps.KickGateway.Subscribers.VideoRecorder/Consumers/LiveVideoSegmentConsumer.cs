using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subscribers.VideoRecorder.Consumers;

/// <summary>Hands each received live-video segment to the <see cref="VideoFileAssembler"/>.</summary>
public sealed class LiveVideoSegmentConsumer : IConsumer<LiveVideoSegment>
{
    private readonly VideoFileAssembler _assembler;

    public LiveVideoSegmentConsumer(VideoFileAssembler assembler) => _assembler = assembler;

    public Task Consume(ConsumeContext<LiveVideoSegment> context) =>
        _assembler.WriteAsync(context.Message, context.CancellationToken);
}
