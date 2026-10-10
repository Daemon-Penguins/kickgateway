namespace TailoredApps.KickGateway.Contracts.Realtime.Media;

/// <summary>
/// A translation of one <see cref="LiveTranscript"/> slice into another language, produced by the
/// <c>Subscribers.Translator</c> service (an LLM/MT call per slice) and published on its own topic
/// exchange, routed by <see cref="BroadcasterSlug"/>. Correlate with the transcript on
/// (<see cref="BroadcasterSlug"/>, <see cref="StartedAt"/>, <see cref="AudioStartSeconds"/>) — the same
/// triple the Api's <c>LiveTranscripts.DedupeKey</c> is built from. Best-effort like the transcript
/// itself: a slice that is noise, too old, or fails at the provider simply has no translation.
/// </summary>
public record LiveTranscriptTranslation
{
    /// <summary>Channel slug (lowercase) — the routing key.</summary>
    public string BroadcasterSlug { get; init; } = "";

    /// <summary>Kick numeric channel id (as carried on the source transcript).</summary>
    public string KickChannelId { get; init; } = "";

    /// <summary>The source transcript's <see cref="LiveTranscript.StartedAt"/>.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>The source transcript's <see cref="LiveTranscript.EndedAt"/>.</summary>
    public DateTime EndedAt { get; init; }

    /// <summary>The source transcript's <see cref="LiveTranscript.AudioStartSeconds"/>.</summary>
    public double AudioStartSeconds { get; init; }

    /// <summary>ISO-639-1 language the slice was transcribed in.</summary>
    public string SourceLanguage { get; init; } = "";

    /// <summary>ISO-639-1 language of <see cref="Text"/>.</summary>
    public string TargetLanguage { get; init; } = "";

    /// <summary>The whole slice translated (segments joined with a space).</summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// Translated segments carrying the <b>source segments' timings</b>, so a caption can be timed exactly
    /// like the original. When the provider's output could not be aligned line by line this holds a single
    /// segment spanning the whole slice.
    /// </summary>
    public LiveTranscriptSegment[] Segments { get; init; } = [];

    /// <summary>Who produced it, e.g. <c>anthropic/claude-haiku-5-5</c> — for comparing quality and cost.</summary>
    public string Provider { get; init; } = "";

    /// <summary>When the translation was produced (server clock, UTC).</summary>
    public DateTime TranslatedAt { get; init; }

    /// <summary>Wall-clock seconds the provider call took.</summary>
    public double ProcessingSeconds { get; init; }
}
