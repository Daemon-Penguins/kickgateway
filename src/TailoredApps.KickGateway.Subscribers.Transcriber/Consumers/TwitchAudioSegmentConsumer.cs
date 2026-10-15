using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.TwitchGateway.Contracts.Media;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Consumers;

/// <summary>
/// Twitch counterpart of <see cref="LiveVideoSegmentConsumer"/>: hands each <see cref="TwitchAudioSegment"/> to the
/// per-channel session. The session pipeline (ffmpeg, chunker, timeline) is platform-neutral and keyed by the
/// channel name, so the segment is carried in the <see cref="LiveVideoSegment"/> shape it already understands.
/// Twitch stamps every segment with <c>PROGRAM-DATE-TIME</c>, so the timeline clock is the segment's real end
/// time rather than the capture time.
/// </summary>
public sealed class TwitchAudioSegmentConsumer : IConsumer<TwitchAudioSegment>
{
    private readonly TranscriptionCoordinator _coordinator;

    public TwitchAudioSegmentConsumer(TranscriptionCoordinator coordinator) => _coordinator = coordinator;

    public Task Consume(ConsumeContext<TwitchAudioSegment> context) =>
        _coordinator.HandleSegmentAsync(ToSession(context.Message), context.CancellationToken);

    /// <summary>Public so the mapping is unit-tested: the login becomes the session key, the user id the channel id, PDT + duration the clock.</summary>
    public static LiveVideoSegment ToSession(TwitchAudioSegment m) => new()
    {
        BroadcasterSlug = (m.BroadcasterLogin ?? "").ToLowerInvariant(),
        KickChannelId = m.BroadcasterUserId ?? "",
        MediaSequence = m.MediaSequence,
        DiscontinuitySequence = m.DiscontinuitySequence,
        Duration = m.Duration,
        IsInitSegment = false,
        VariantBandwidth = 0,
        ContentType = string.IsNullOrEmpty(m.ContentType) ? "video/mp2t" : m.ContentType,
        SegmentUri = m.SegmentUri ?? "",
        CapturedAt = m.ProgramDateTime is { } pdt ? pdt.AddSeconds(m.Duration) : m.CapturedAt,
        Data = m.Data ?? [],
    };
}
