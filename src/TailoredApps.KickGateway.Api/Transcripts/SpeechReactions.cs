using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Transcripts;

/// <summary>A chat message inside the reaction window of a speech slice.</summary>
public sealed record ReactionMessage(
    string MessageId,
    DateTime CreatedAt,
    /// <summary>Seconds after the slice's <c>endedAt</c>.</summary>
    double SecondsAfterEnd,
    string UserId,
    string Username,
    string Text,
    string? ReplyToMessageId,
    string? ReplyToUsername);

public sealed record ReactionStats(
    /// <summary>Messages in the reaction window (exact, not capped by <c>limit</c>).</summary>
    int Messages,
    int Chatters,
    double PerMinute,
    /// <summary>Messages in the 60 s before the slice started — chat that cannot be a reaction to it.</summary>
    int BaselineMessages,
    double BaselinePerMinute,
    /// <summary><see cref="PerMinute"/> / <see cref="BaselinePerMinute"/>; null when the baseline minute was silent.</summary>
    double? Ratio,
    ReactionMessage? FirstReply);

/// <summary>What chat wrote right after one speech slice — the forward direction of <c>/transcripts/at</c>.</summary>
public sealed record SpeechReactions(
    string Channel,
    TranscriptDto Slice,
    TranscriptDto? Previous,
    TranscriptDto? Next,
    /// <summary>The reaction window: <c>[endedAt + lagMin, endedAt + lagMax)</c>.</summary>
    AnalyticsWindow Window,
    double LagMinSeconds,
    double LagMaxSeconds,
    ReactionStats Stats,
    IReadOnlyList<ReactionMessage> Messages,
    /// <summary>True when more messages fell in the window than <c>limit</c> allowed to return.</summary>
    bool Truncated,
    string Chronology);

/// <summary>
/// "What did the streamer say and what did chat answer": one slice, its neighbours, and the chat that followed it.
/// Six bounded seeks on existing indexes (slice by key, neighbours on <c>(ChannelSlug, StartedAt)</c>, the window and the
/// baseline minute on <c>(ChannelSlug, CreatedAt)</c>) — never a scan, however busy the channel.
/// </summary>
public static class SpeechReactionQueries
{
    public const double DefaultLagMin = 5;
    public const double DefaultLagMax = 45;
    public const double MaxLag = 600;
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;
    private static readonly TimeSpan BaselineSpan = TimeSpan.FromSeconds(60);

    public const string ChronologyNote =
        "Speech reaches viewers ~10-20 s after it is said (HLS latency plus the player's buffer), so chat reactions to a " +
        "slice land in [endedAt + lagMin, endedAt + lagMax). 'baseline' is the 60 s before the slice started - chat that " +
        "cannot be a reaction to it; 'ratio' > 1 means chat sped up. Messages in the window may also answer the previous " +
        "slice or each other; read them before concluding what they react to.";

    /// <summary>Null when the slice does not exist on that channel.</summary>
    public static async Task<SpeechReactions?> ForSliceAsync(
        KickGatewayDbContext db, string platform, string channel, long id, double lagMinSeconds, double lagMaxSeconds, int limit, CancellationToken ct)
    {
        var slice = await db.LiveTranscripts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Platform == platform && x.ChannelSlug == channel, ct);
        if (slice is null) return null;

        var previous = await db.LiveTranscripts.AsNoTracking()
            .Where(x => x.Platform == platform && x.ChannelSlug == channel && (x.StartedAt < slice.StartedAt || (x.StartedAt == slice.StartedAt && x.Id < slice.Id)))
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        var next = await db.LiveTranscripts.AsNoTracking()
            .Where(x => x.Platform == platform && x.ChannelSlug == channel && (x.StartedAt > slice.StartedAt || (x.StartedAt == slice.StartedAt && x.Id > slice.Id)))
            .OrderBy(x => x.StartedAt).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);

        var from = slice.EndedAt.AddSeconds(lagMinSeconds);
        var to = slice.EndedAt.AddSeconds(lagMaxSeconds);
        var inWindow = db.ChatMessages.AsNoTracking().Where(m => m.Platform == platform && m.ChannelSlug == channel && m.CreatedAt >= from && m.CreatedAt < to);
        var count = await inWindow.CountAsync(ct);
        var chatters = count == 0 ? 0 : await inWindow.Select(m => m.SenderUserId).Distinct().CountAsync(ct);
        var rows = count == 0
            ? []
            : await inWindow
                .OrderBy(m => m.CreatedAt).ThenBy(m => m.MessageId)
                .Take(limit)
                .Select(m => new { m.MessageId, m.CreatedAt, m.SenderUserId, m.SenderUsername, m.Content, m.ReplyToMessageId, m.ReplyToUsername })
                .ToListAsync(ct);

        var baselineFrom = slice.StartedAt - BaselineSpan;
        var baseline = await db.ChatMessages.AsNoTracking()
            .CountAsync(m => m.Platform == platform && m.ChannelSlug == channel && m.CreatedAt >= baselineFrom && m.CreatedAt < slice.StartedAt, ct);

        var slices = new List<LiveTranscriptRecord>(3) { slice };
        if (previous is not null) slices.Add(previous);
        if (next is not null) slices.Add(next);
        var translations = await TranscriptQueries.TranslationsForAsync(db, slices, ct);

        var messages = rows.Select(m => new ReactionMessage(
            m.MessageId, m.CreatedAt, Math.Round((m.CreatedAt - slice.EndedAt).TotalSeconds, 1),
            m.SenderUserId, m.SenderUsername, m.Content, m.ReplyToMessageId, m.ReplyToUsername)).ToList();

        var windowSeconds = lagMaxSeconds - lagMinSeconds;
        var perMinute = Math.Round(count * 60.0 / windowSeconds, 2);
        var baselinePerMinute = baseline * 60.0 / BaselineSpan.TotalSeconds;
        var stats = new ReactionStats(
            count, chatters, perMinute, baseline, baselinePerMinute,
            baseline == 0 ? null : Math.Round(perMinute / baselinePerMinute, 2),
            messages.Count > 0 ? messages[0] : null);

        return new SpeechReactions(
            channel,
            TranscriptQueries.ToDto(slice, translations, includeSegments: false),
            previous is null ? null : TranscriptQueries.ToDto(previous, translations, includeSegments: false),
            next is null ? null : TranscriptQueries.ToDto(next, translations, includeSegments: false),
            new AnalyticsWindow(from, to),
            lagMinSeconds, lagMaxSeconds,
            stats, messages, count > messages.Count, ChronologyNote);
    }
}
