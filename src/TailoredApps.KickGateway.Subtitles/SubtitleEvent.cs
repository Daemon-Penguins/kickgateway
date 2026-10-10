using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subtitles;

/// <summary>One Whisper segment inside a <see cref="SubtitleEvent"/>.</summary>
public sealed record SubtitleSegment(DateTime StartedAt, DateTime EndedAt, string Text, float Confidence);

/// <summary>
/// What the page receives for every <see cref="LiveTranscript"/>: the slice plus the two things the
/// browser cannot know — a per-channel sequence id (SSE <c>Last-Event-ID</c> resume) and how far the
/// text already trails the audio on the gateway's clock. <see cref="Translations"/> /
/// <see cref="TranslatedSegments"/> (target language → text / timed segments) are filled in when a
/// <see cref="LiveTranscriptTranslation"/> arrives; the page falls back to <see cref="Text"/>.
/// </summary>
public sealed record SubtitleEvent(
    long Id,
    string Slug,
    DateTime StartedAt,
    DateTime EndedAt,
    double AudioSeconds,
    double AudioStartSeconds,
    string Text,
    string Language,
    string? DetectedLanguage,
    float? LanguageProbability,
    float Confidence,
    SubtitleSegment[] Segments,
    DateTime TranscribedAt,
    DateTime ReceivedAt,
    double LagSeconds,
    IReadOnlyDictionary<string, string>? Translations,
    IReadOnlyDictionary<string, SubtitleSegment[]>? TranslatedSegments)
{
    public static SubtitleEvent From(LiveTranscript t, string slug, long id, DateTime receivedAt)
    {
        return new SubtitleEvent(
            id,
            slug,
            t.StartedAt,
            t.EndedAt,
            t.AudioSeconds,
            t.AudioStartSeconds,
            (t.Text ?? "").Trim(),
            (t.Language ?? "").Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(t.DetectedLanguage) ? null : t.DetectedLanguage.Trim().ToLowerInvariant(),
            t.LanguageProbability,
            t.Confidence,
            ToSegments(t.Segments),
            t.TranscribedAt,
            receivedAt,
            Math.Max(0, (receivedAt - t.EndedAt).TotalSeconds),
            null,
            null);
    }

    /// <summary>A copy with one more translation attached (an existing one for the same language is replaced).</summary>
    public SubtitleEvent WithTranslation(string language, string text, LiveTranscriptSegment[]? segments)
    {
        var texts = new Dictionary<string, string>(Translations ?? new Dictionary<string, string>(), StringComparer.Ordinal) { [language] = text };
        var segs = new Dictionary<string, SubtitleSegment[]>(TranslatedSegments ?? new Dictionary<string, SubtitleSegment[]>(), StringComparer.Ordinal) { [language] = ToSegments(segments) };
        return this with { Translations = texts, TranslatedSegments = segs };
    }

    /// <summary>True when the transcript this translation refers to is this event (same slug + start + audio-clock position).</summary>
    public bool Matches(DateTime startedAt, double audioStartSeconds) =>
        StartedAt == startedAt && Math.Abs(AudioStartSeconds - audioStartSeconds) < 0.0005;

    private static SubtitleSegment[] ToSegments(LiveTranscriptSegment[]? segments) => (segments ?? [])
        .Select(s => new SubtitleSegment(s.StartedAt, s.EndedAt, (s.Text ?? "").Trim(), s.Confidence))
        .Where(s => s.Text.Length > 0)
        .ToArray();
}

/// <summary>What a subscriber receives: a new transcript, or an existing one that gained a translation.</summary>
/// <param name="Kind">SSE event name: <c>transcript</c> or <c>translation</c>.</param>
public sealed record FeedMessage(string Kind, SubtitleEvent Event)
{
    public const string Transcript = "transcript";
    public const string Translation = "translation";
}
