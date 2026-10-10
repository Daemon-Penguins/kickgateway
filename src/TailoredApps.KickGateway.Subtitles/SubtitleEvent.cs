using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subtitles;

/// <summary>One Whisper segment inside a <see cref="SubtitleEvent"/>.</summary>
public sealed record SubtitleSegment(DateTime StartedAt, DateTime EndedAt, string Text, float Confidence);

/// <summary>
/// What the page receives for every <see cref="LiveTranscript"/>: the slice plus the two things the
/// browser cannot know — a per-channel sequence id (SSE <c>Last-Event-ID</c> resume) and how far the
/// text already trails the audio on the gateway's clock. <see cref="Translations"/> is the slot for the
/// upcoming translation layer (target language → text); the page falls back to <see cref="Text"/>.
/// </summary>
public sealed record SubtitleEvent(
    long Id,
    string Slug,
    DateTime StartedAt,
    DateTime EndedAt,
    double AudioSeconds,
    string Text,
    string Language,
    string? DetectedLanguage,
    float? LanguageProbability,
    float Confidence,
    SubtitleSegment[] Segments,
    DateTime TranscribedAt,
    DateTime ReceivedAt,
    double LagSeconds,
    IReadOnlyDictionary<string, string>? Translations)
{
    public static SubtitleEvent From(LiveTranscript t, string slug, long id, DateTime receivedAt)
    {
        var segments = (t.Segments ?? [])
            .Select(s => new SubtitleSegment(s.StartedAt, s.EndedAt, (s.Text ?? "").Trim(), s.Confidence))
            .Where(s => s.Text.Length > 0)
            .ToArray();

        return new SubtitleEvent(
            id,
            slug,
            t.StartedAt,
            t.EndedAt,
            t.AudioSeconds,
            (t.Text ?? "").Trim(),
            (t.Language ?? "").Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(t.DetectedLanguage) ? null : t.DetectedLanguage.Trim().ToLowerInvariant(),
            t.LanguageProbability,
            t.Confidence,
            segments,
            t.TranscribedAt,
            receivedAt,
            Math.Max(0, (receivedAt - t.EndedAt).TotalSeconds),
            null);
    }
}
