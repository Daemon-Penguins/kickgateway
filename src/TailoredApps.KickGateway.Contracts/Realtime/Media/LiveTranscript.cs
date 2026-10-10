namespace TailoredApps.KickGateway.Contracts.Realtime.Media;

/// <summary>
/// Speech-to-text of a slice (~10–30 s) of a channel's <b>live</b> audio, produced by the
/// <c>Subscribers.Transcriber</c> service from the <see cref="LiveVideoSegment"/> firehose
/// (ffmpeg → 16 kHz mono → Whisper). Published on its own topic exchange, routed by
/// <see cref="BroadcasterSlug"/> like every other Kick contract, so a subscriber can bind
/// <c>#</c> or a single slug.
/// <para>
/// Timestamps are <b>estimates</b> derived from the HLS segment timeline (segment duration +
/// capture time), accurate to roughly one segment (a few seconds). Transcripts are best-effort:
/// when the transcriber falls behind it drops the oldest pending audio to stay live, so there can
/// be gaps between consecutive messages.
/// </para>
/// </summary>
public record LiveTranscript
{
    /// <summary>Channel slug (lowercase) — the routing key.</summary>
    public string BroadcasterSlug { get; init; } = "";

    /// <summary>Kick numeric channel id (as carried on the source <see cref="LiveVideoSegment"/>).</summary>
    public string KickChannelId { get; init; } = "";

    /// <summary>Estimated UTC time the transcribed audio started (stream time, not processing time).</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Estimated UTC time the transcribed audio ended.</summary>
    public DateTime EndedAt { get; init; }

    /// <summary>
    /// Position of this slice on the transcriber's per-channel audio clock, in seconds since the
    /// channel's capture session started. Monotonic within a session; useful for ordering and gap
    /// detection.
    /// </summary>
    public double AudioStartSeconds { get; init; }

    /// <summary>Length of the transcribed audio in seconds.</summary>
    public double AudioSeconds { get; init; }

    /// <summary>The recognized text of the whole slice (segments joined with a space), trimmed.</summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// ISO-639-1 language the slice was transcribed in. With language detection on (the default:
    /// <c>Transcriber:Languages</c>) this is the channel's current language — switched when the detector is
    /// confident, kept when it is not; otherwise the configured language, or Whisper's own pick in <c>auto</c> mode.
    /// </summary>
    public string Language { get; init; } = "";

    /// <summary>
    /// What Whisper's language detector heard on this slice (ISO-639-1), when detection ran; null otherwise
    /// (fixed language, slice too short to judge). Can differ from <see cref="Language"/>: an unsure reading
    /// does not switch the channel, so the text is still in the previous language.
    /// </summary>
    public string? DetectedLanguage { get; init; }

    /// <summary>Detector probability for <see cref="DetectedLanguage"/>, 0..1; null when detection did not run.</summary>
    public float? LanguageProbability { get; init; }

    /// <summary>Average token probability over the kept segments, 0..1 (0 when unknown).</summary>
    public float Confidence { get; init; }

    /// <summary>Finer-grained pieces as Whisper emitted them, in order (each with its own estimated times).</summary>
    public LiveTranscriptSegment[] Segments { get; init; } = [];

    /// <summary>First HLS <c>EXT-X-MEDIA-SEQUENCE</c> that contributed audio to this slice.</summary>
    public long FirstMediaSequence { get; init; }

    /// <summary>Last HLS <c>EXT-X-MEDIA-SEQUENCE</c> that contributed audio to this slice.</summary>
    public long LastMediaSequence { get; init; }

    /// <summary>Whisper model that produced the text, e.g. <c>LargeV3Turbo/Q5_0</c>.</summary>
    public string Model { get; init; } = "";

    /// <summary>When the transcriber finished this slice (server clock, UTC).</summary>
    public DateTime TranscribedAt { get; init; }

    /// <summary>Wall-clock seconds Whisper spent on this slice — lets consumers gauge how far behind live the transcript runs.</summary>
    public double ProcessingSeconds { get; init; }
}

/// <summary>One Whisper segment inside a <see cref="LiveTranscript"/>.</summary>
public record LiveTranscriptSegment
{
    /// <summary>Estimated UTC start of this piece.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Estimated UTC end of this piece.</summary>
    public DateTime EndedAt { get; init; }

    /// <summary>Recognized text, trimmed.</summary>
    public string Text { get; init; } = "";

    /// <summary>Average token probability, 0..1 (0 when unknown).</summary>
    public float Confidence { get; init; }
}
