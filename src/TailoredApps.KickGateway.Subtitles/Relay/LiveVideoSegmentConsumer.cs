using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subtitles.Relay;

/// <summary>Bus → <see cref="VideoRelay"/>. Segments outside the relay's allowlist are dropped on the floor.</summary>
public sealed class LiveVideoSegmentConsumer(VideoRelay relay, ILogger<LiveVideoSegmentConsumer> log) : IConsumer<LiveVideoSegment>
{
    public Task Consume(ConsumeContext<LiveVideoSegment> context)
    {
        var m = context.Message;
        if (relay.Ingest(m))
            log.LogTrace("[{Slug}] buffered segment #{Seq} ({Bytes} bytes, {Dur:F1}s)", m.BroadcasterSlug, m.MediaSequence, m.Data?.Length ?? 0, m.Duration);
        return Task.CompletedTask;
    }
}
