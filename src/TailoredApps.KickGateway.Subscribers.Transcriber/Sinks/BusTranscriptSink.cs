using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.TwitchGateway.Contracts.Media;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Sinks;

/// <summary>
/// Publishes each transcript on its channel-routed topic exchange: <see cref="LiveTranscript"/> (see
/// <see cref="KickMediaTopology"/>) when the source is Kick, <see cref="TwitchLiveTranscript"/> (see
/// <see cref="TwitchMediaTopology"/>) when it is Twitch. Direct <see cref="IBus"/> publish — this service has
/// no database and therefore no outbox; transcripts are small and carry no TTL.
/// </summary>
public sealed class BusTranscriptSink : ITranscriptSink
{
    private readonly IBus _bus;
    private readonly bool _twitch;

    public BusTranscriptSink(IBus bus, TranscriberOptions opts)
    {
        _bus = bus;
        _twitch = opts.IsTwitch;
    }

    public string Name => _twitch ? "bus (twitch)" : "bus";

    public Task WriteAsync(LiveTranscript transcript, CancellationToken ct) =>
        _twitch ? _bus.Publish(ToTwitch(transcript), ct) : _bus.Publish(transcript, ct);

    /// <summary>The internal transcript (Kick contract shape) as the Twitch contract. Public for tests.</summary>
    public static TwitchLiveTranscript ToTwitch(LiveTranscript t) => new()
    {
        BroadcasterLogin = t.BroadcasterSlug,
        BroadcasterUserId = t.KickChannelId,
        StartedAt = t.StartedAt,
        EndedAt = t.EndedAt,
        AudioStartSeconds = t.AudioStartSeconds,
        AudioSeconds = t.AudioSeconds,
        Text = t.Text,
        Language = t.Language,
        DetectedLanguage = t.DetectedLanguage,
        LanguageProbability = t.LanguageProbability,
        Confidence = t.Confidence,
        Segments = (t.Segments ?? []).Select(s => new TwitchTranscriptSegment
        {
            StartedAt = s.StartedAt,
            EndedAt = s.EndedAt,
            Text = s.Text,
            Confidence = s.Confidence,
        }).ToArray(),
        FirstMediaSequence = t.FirstMediaSequence,
        LastMediaSequence = t.LastMediaSequence,
        Model = t.Model,
        TranscribedAt = t.TranscribedAt,
        ProcessingSeconds = t.ProcessingSeconds,
    };
}
