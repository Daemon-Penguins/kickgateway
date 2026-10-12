using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Transcripts;

/// <summary>How much speech-to-text a channel has inside a window (slices that START in the window).</summary>
public sealed record TranscriptWindowCoverage(
    AnalyticsWindow Window,
    int Slices,
    double SpeechSeconds,
    double? AverageConfidence,
    DateTime? FirstStartedAt,
    DateTime? LastStartedAt,
    /// <summary>ISO-639-1 language the slices were transcribed in → slice count, most common first.</summary>
    IReadOnlyDictionary<string, int> Languages);

/// <summary>One channel's transcript coverage for <c>/status</c>: all-time extent, freshness, and the last 24 h in detail.</summary>
public sealed record TranscriptChannelCoverage(
    string Channel,
    long Slices,
    DateTime FirstStartedAt,
    DateTime LastStartedAt,
    DateTime LastEndedAt,
    DateTime LastReceivedAt,
    /// <summary>How long after the newest slice ended the Api stored it — the pipeline's end-to-end delay.</summary>
    double LastReceiveLagSeconds,
    TranscriptWindowCoverage Last24h);

public sealed record TranscriptCoverage(long Slices, IReadOnlyList<TranscriptChannelCoverage> Channels);

/// <summary>
/// Lets the model know whether speech exists at all before it concludes "the streamer never talked about it".
/// Per channel: one grouped pass over the <c>(ChannelSlug, StartedAt)</c> index (count/min/max), one seek for the
/// newest slice, one bounded range for the last 24 h grouped by language.
/// </summary>
public static class TranscriptCoverageQueries
{
    public static readonly TimeSpan RecentSpan = TimeSpan.FromHours(24);

    public static async Task<TranscriptCoverage> StatusAsync(KickGatewayDbContext db, AnalyticsScope scope, DateTime now, CancellationToken ct)
    {
        IQueryable<LiveTranscriptRecord> q = db.LiveTranscripts.AsNoTracking();
        if (scope.Slugs is { } slugs) q = q.Where(x => slugs.Contains(x.ChannelSlug));

        var perChannel = await q
            .GroupBy(x => x.ChannelSlug)
            .Select(g => new { Slug = g.Key, Slices = g.LongCount(), First = g.Min(x => x.StartedAt), Last = g.Max(x => x.StartedAt) })
            .OrderBy(x => x.Slug)
            .ToListAsync(ct);

        var recent = new AnalyticsWindow(now - RecentSpan, now);
        var channels = new List<TranscriptChannelCoverage>(perChannel.Count);
        foreach (var c in perChannel)
        {
            var newest = await db.LiveTranscripts.AsNoTracking()
                .Where(x => x.ChannelSlug == c.Slug)
                .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
                .Select(x => new { x.EndedAt, x.ReceivedAt })
                .FirstAsync(ct);
            channels.Add(new TranscriptChannelCoverage(
                c.Slug, c.Slices, c.First, c.Last, newest.EndedAt, newest.ReceivedAt,
                Math.Round((newest.ReceivedAt - newest.EndedAt).TotalSeconds, 1),
                await WindowAsync(db, c.Slug, recent, ct)));
        }
        return new TranscriptCoverage(perChannel.Sum(c => c.Slices), channels);
    }

    public static async Task<TranscriptWindowCoverage> WindowAsync(KickGatewayDbContext db, string channel, AnalyticsWindow window, CancellationToken ct)
    {
        IQueryable<LiveTranscriptRecord> q = db.LiveTranscripts.AsNoTracking().Where(x => x.ChannelSlug == channel && x.StartedAt < window.To);
        if (window.From is { } from) q = q.Where(x => x.StartedAt >= from);

        var rows = await q
            .GroupBy(x => x.Language)
            .Select(g => new
            {
                Language = g.Key,
                Slices = g.Count(),
                Seconds = g.Sum(x => x.AudioSeconds),
                Confidence = g.Average(x => (double)x.Confidence),
                First = g.Min(x => x.StartedAt),
                Last = g.Max(x => x.StartedAt),
            })
            .ToListAsync(ct);

        var slices = rows.Sum(r => r.Slices);
        return new TranscriptWindowCoverage(
            window,
            slices,
            Math.Round(rows.Sum(r => r.Seconds), 1),
            slices == 0 ? null : Math.Round(rows.Sum(r => r.Confidence * r.Slices) / slices, 3),
            rows.Count == 0 ? null : rows.Min(r => r.First),
            rows.Count == 0 ? null : rows.Max(r => r.Last),
            rows.OrderByDescending(r => r.Slices).ThenBy(r => r.Language, StringComparer.Ordinal)
                .ToDictionary(r => r.Language, r => r.Slices, StringComparer.Ordinal));
    }
}
