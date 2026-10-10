using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Api.Transcripts;

/// <summary>One Whisper segment inside a stored transcript (same shape as the contract's segment).</summary>
public sealed record TranscriptSegmentDto(DateTime StartedAt, DateTime EndedAt, string Text, float Confidence);

/// <summary>A stored live-transcript slice as returned by <c>/api/analytics/channels/{slug}/transcripts</c>.</summary>
public sealed record TranscriptDto(
    long Id,
    string Channel,
    DateTime StartedAt,
    DateTime EndedAt,
    double AudioStartSeconds,
    double AudioSeconds,
    string Text,
    string Language,
    string? DetectedLanguage,
    float? LanguageProbability,
    float Confidence,
    IReadOnlyList<TranscriptSegmentDto> Segments,
    long FirstMediaSequence,
    long LastMediaSequence,
    string Model,
    DateTime TranscribedAt,
    double ProcessingSeconds);

/// <summary>A page of transcripts, oldest first. Pass <see cref="NextCursor"/> back as <c>cursor</c> for the next page. <see cref="Language"/> echoes the language filter, if any.</summary>
public sealed record TranscriptPage(string Channel, AnalyticsWindow Window, string? Query, string? Language, IReadOnlyList<TranscriptDto> Items, string? NextCursor);

/// <summary>JSON shape used both for the stored <see cref="LiveTranscriptRecord.SegmentsJson"/> and for reading it back.</summary>
public static class TranscriptJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializeSegments(LiveTranscriptSegment[] segments) => JsonSerializer.Serialize(segments, Options);

    public static IReadOnlyList<TranscriptSegmentDto> ReadSegments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<TranscriptSegmentDto[]>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Read side of the stored transcripts: windowed, optionally text-filtered, keyset-paged.</summary>
public static class TranscriptQueries
{
    /// <summary>
    /// Transcripts of <paramref name="channel"/> overlapping <paramref name="window"/>, oldest first.
    /// <paramref name="query"/> is a case-insensitive substring match on the text (SQL <c>LIKE</c>);
    /// <paramref name="language"/> keeps only slices transcribed in that ISO-639-1 language.
    /// </summary>
    public static async Task<TranscriptPage> PageAsync(
        KickGatewayDbContext db, string channel, AnalyticsWindow window, string? query, string? language, string? cursor, int limit, CancellationToken ct)
    {
        IQueryable<LiveTranscriptRecord> q = db.LiveTranscripts.AsNoTracking()
            .Where(x => x.ChannelSlug == channel && x.StartedAt < window.To);
        if (window.From is { } from) q = q.Where(x => x.EndedAt >= from);

        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (text is not null)
        {
            var pattern = "%" + EscapeLike(text) + "%";
            q = q.Where(x => EF.Functions.Like(x.Text, pattern, "\\"));
        }

        var lang = string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();
        if (lang is not null) q = q.Where(x => x.Language == lang);

        if (TryParseCursor(cursor, out var afterStarted, out var afterId))
            q = q.Where(x => x.StartedAt > afterStarted || (x.StartedAt == afterStarted && x.Id > afterId));

        var rows = await q.OrderBy(x => x.StartedAt).ThenBy(x => x.Id).Take(limit + 1).ToListAsync(ct);

        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = FormatCursor(last.StartedAt, last.Id);
        }

        return new TranscriptPage(channel, window, text, lang, rows.Select(ToDto).ToList(), next);
    }

    /// <summary>What was being said on <paramref name="channel"/> at <paramref name="at"/> (± <paramref name="toleranceSeconds"/>), oldest first.</summary>
    public static async Task<IReadOnlyList<TranscriptDto>> AroundAsync(
        KickGatewayDbContext db, string channel, DateTime at, double toleranceSeconds, CancellationToken ct)
    {
        var from = at.AddSeconds(-toleranceSeconds);
        var to = at.AddSeconds(toleranceSeconds);
        var rows = await db.LiveTranscripts.AsNoTracking()
            .Where(x => x.ChannelSlug == channel && x.StartedAt <= to && x.EndedAt >= from)
            .OrderBy(x => x.StartedAt).ThenBy(x => x.Id)
            .Take(20)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public static TranscriptDto ToDto(LiveTranscriptRecord r) => new(
        r.Id, r.ChannelSlug, r.StartedAt, r.EndedAt, r.AudioStartSeconds, r.AudioSeconds, r.Text, r.Language, r.DetectedLanguage, r.LanguageProbability, r.Confidence,
        TranscriptJson.ReadSegments(r.SegmentsJson), r.FirstMediaSequence, r.LastMediaSequence, r.Model, r.TranscribedAt, r.ProcessingSeconds);

    private static string FormatCursor(DateTime startedAt, long id) =>
        startedAt.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseCursor(string? cursor, out DateTime startedAt, out long id)
    {
        startedAt = default;
        id = 0;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        var parts = cursor.Split(':', 2);
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id)) return false;
        startedAt = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
