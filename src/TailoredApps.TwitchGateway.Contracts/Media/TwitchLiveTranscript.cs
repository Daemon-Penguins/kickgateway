namespace TailoredApps.TwitchGateway.Contracts.Media;

/// <summary>
/// Speech-to-text of a slice (~10-30 s) of a Twitch channel's live audio, produced by the transcriber
/// running with <c>Transcriber:Source=twitch</c> from the <see cref="TwitchAudioSegment"/> stream.
/// Same semantics as the Kick <c>LiveTranscript</c>: timestamps are stream time estimated from the
/// segment timeline (Twitch's per-segment <c>PROGRAM-DATE-TIME</c> makes them exact to a segment),
/// slices are best-effort and may have gaps. Published on its own topic exchange, routed by
/// <see cref="BroadcasterLogin"/>.
/// </summary>
public record TwitchLiveTranscript
{
    public string BroadcasterLogin { get; init; } = "";
    public string BroadcasterUserId { get; init; } = "";

    /// <summary>Estimated UTC stream time the transcribed audio started.</summary>
    public DateTime StartedAt { get; init; }

    public DateTime EndedAt { get; init; }

    /// <summary>Position on the transcriber's per-channel audio clock (seconds since the capture session started).</summary>
    public double AudioStartSeconds { get; init; }

    public double AudioSeconds { get; init; }

    public string Text { get; init; } = "";

    /// <summary>ISO-639-1 language the slice was transcribed in.</summary>
    public string Language { get; init; } = "";

    public string? DetectedLanguage { get; init; }
    public float? LanguageProbability { get; init; }
    public float Confidence { get; init; }
    public TwitchTranscriptSegment[] Segments { get; init; } = [];
    public long FirstMediaSequence { get; init; }
    public long LastMediaSequence { get; init; }
    public string Model { get; init; } = "";
    public DateTime TranscribedAt { get; init; }
    public double ProcessingSeconds { get; init; }
}

/// <summary>One Whisper segment inside a <see cref="TwitchLiveTranscript"/>.</summary>
public record TwitchTranscriptSegment
{
    public DateTime StartedAt { get; init; }
    public DateTime EndedAt { get; init; }
    public string Text { get; init; } = "";
    public float Confidence { get; init; }
}
