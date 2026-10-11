using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>
/// SQL side of the channel timeline (<see cref="TimelinePage"/>). One page costs at most three bounded
/// index seeks, one per kind (<c>(ChannelSlug, StartedAt)</c>, <c>(ChannelSlug, OccurredAt)</c>,
/// <c>(ChannelSlug, CreatedAt)</c>, each <c>TOP limit+1</c> in index order; the clustered key completes the
/// ordering so no sort is needed) plus one range read of the translations for the speech rows on the page.
/// Never a join across the tables, never a scan: the chat tables are large and the projector keeps writing
/// while we read (the request runs READ UNCOMMITTED like every analytics query).
/// </summary>
public static class TimelineQueries
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    /// <summary>Default look-back when neither <c>from</c> nor <c>at</c> is given.</summary>
    public static readonly TimeSpan DefaultLookBack = TimeSpan.FromHours(1);

    /// <summary>A slice may start this long before the window and still end inside it (slices are 10-30 s; generous on purpose).</summary>
    public static readonly TimeSpan SliceGrace = TimeSpan.FromSeconds(120);

    public const string Chronology =
        "Items are ordered by stream time (UTC). speech.at = estimated time the slice started (+/- one HLS segment), " +
        "endedAt when it ended; chat.at = when the message was sent. Viewers watch with a player delay, so chat " +
        "reactions to a sentence usually land 10-20 s after its endedAt. Speech is best-effort: a gap means nothing " +
        "was transcribed, not that nobody spoke.";

    public static async Task<TimelinePage> PageAsync(KickGatewayDbContext db, string channel, AnalyticsWindow window,
        IReadOnlySet<string> kinds, float minConfidence, string? cursor, int limit, CancellationToken ct)
    {
        TimelineCursor? after = TimelineCursor.TryParse(cursor, out var parsed) ? parsed : null;
        var take = limit + 1;

        var sources = new List<IReadOnlyList<TimelineItem>>(3);
        if (kinds.Contains(TimelineKinds.Speech)) sources.Add(await SpeechAsync(db, channel, window, minConfidence, after, take, ct));
        if (kinds.Contains(TimelineKinds.Event)) sources.Add(await EventsAsync(db, channel, window, after, take, ct));
        if (kinds.Contains(TimelineKinds.Chat)) sources.Add(await ChatAsync(db, channel, window, after, take, ct));

        var (items, next) = TimelineMerge.Page(sources, limit);
        return new TimelinePage(channel, window, TimelineKinds.All.Where(kinds.Contains).ToList(), minConfidence, items, next, Chronology);
    }

    /// <summary>Slices overlapping the window (they may start up to <see cref="SliceGrace"/> before it), by start time.</summary>
    private static async Task<IReadOnlyList<TimelineItem>> SpeechAsync(KickGatewayDbContext db, string channel, AnalyticsWindow window,
        float minConfidence, TimelineCursor? after, int take, CancellationToken ct)
    {
        var rank = TimelineKinds.Rank(TimelineKinds.Speech);
        var q = db.LiveTranscripts.AsNoTracking().Where(x => x.ChannelSlug == channel && x.StartedAt < window.To);
        if (window.From is { } from)
        {
            var earliest = from - SliceGrace;
            q = q.Where(x => x.StartedAt >= earliest && x.EndedAt >= from);
        }
        if (minConfidence > 0) q = q.Where(x => x.Confidence >= minConfidence);
        if (after is { } c)
        {
            if (rank > c.Rank) q = q.Where(x => x.StartedAt >= c.At);
            else if (rank == c.Rank)
            {
                var id = c.SpeechId;
                q = q.Where(x => x.StartedAt > c.At || (x.StartedAt == c.At && x.Id > id));
            }
            else q = q.Where(x => x.StartedAt > c.At);
        }

        var rows = await q.OrderBy(x => x.StartedAt).ThenBy(x => x.Id).Take(take)
            .Select(x => new { x.Id, x.DedupeKey, x.StartedAt, x.EndedAt, x.Text, x.Language, x.Confidence })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        // Translations of exactly these slices: one range seek on (ChannelSlug, StartedAt) instead of an IN-list.
        var first = rows[0].StartedAt;
        var last = rows[^1].StartedAt;
        var keys = rows.Select(r => r.DedupeKey).ToHashSet(StringComparer.Ordinal);
        var translations = (await db.LiveTranscriptTranslations.AsNoTracking()
                .Where(t => t.ChannelSlug == channel && t.StartedAt >= first && t.StartedAt <= last)
                .Select(t => new { t.TranscriptDedupeKey, t.TargetLanguage, t.Text })
                .ToListAsync(ct))
            .Where(t => keys.Contains(t.TranscriptDedupeKey))
            .ToLookup(t => t.TranscriptDedupeKey, StringComparer.Ordinal);

        return rows.Select(r =>
        {
            Dictionary<string, string>? texts = null;
            foreach (var t in translations[r.DedupeKey])
            {
                texts ??= new Dictionary<string, string>(StringComparer.Ordinal);
                texts[t.TargetLanguage] = t.Text;
            }
            return new TimelineItem(TimelineKinds.Speech, r.StartedAt, r.Id.ToString(CultureInfo.InvariantCulture), channel, r.Text,
                EndedAt: r.EndedAt, Language: r.Language, Confidence: r.Confidence, Translations: texts);
        }).ToList();
    }

    private static async Task<IReadOnlyList<TimelineItem>> EventsAsync(KickGatewayDbContext db, string channel, AnalyticsWindow window,
        TimelineCursor? after, int take, CancellationToken ct)
    {
        var rank = TimelineKinds.Rank(TimelineKinds.Event);
        var q = db.ChatterEvents.AsNoTracking().Where(e => e.ChannelSlug == channel && e.OccurredAt < window.To);
        if (window.From is { } from) q = q.Where(e => e.OccurredAt >= from);
        if (after is { } c)
        {
            if (rank > c.Rank) q = q.Where(e => e.OccurredAt >= c.At);
            else if (rank == c.Rank)
            {
                var id = c.Id;
                q = q.Where(e => e.OccurredAt > c.At || (e.OccurredAt == c.At && string.Compare(e.Id, id) > 0));
            }
            else q = q.Where(e => e.OccurredAt > c.At);
        }

        var rows = await q.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).Take(take)
            .Select(e => new { e.Id, e.Kind, e.OccurredAt, e.UserId, e.Username, e.CounterpartUsername, e.Amount, e.Detail })
            .ToListAsync(ct);

        return rows.Select(e => new TimelineItem(TimelineKinds.Event, e.OccurredAt, e.Id, e.Username, e.Detail ?? "",
            UserId: e.UserId.Length == 0 ? null : e.UserId,
            EventKind: EventKindName(e.Kind),
            Counterpart: e.CounterpartUsername,
            Amount: e.Amount == 0 ? null : e.Amount)).ToList();
    }

    private static async Task<IReadOnlyList<TimelineItem>> ChatAsync(KickGatewayDbContext db, string channel, AnalyticsWindow window,
        TimelineCursor? after, int take, CancellationToken ct)
    {
        var rank = TimelineKinds.Rank(TimelineKinds.Chat);
        var q = db.ChatMessages.AsNoTracking().Where(m => m.ChannelSlug == channel && m.CreatedAt < window.To);
        if (window.From is { } from) q = q.Where(m => m.CreatedAt >= from);
        if (after is { } c)
        {
            if (rank > c.Rank) q = q.Where(m => m.CreatedAt >= c.At);
            else if (rank == c.Rank)
            {
                var id = c.Id;
                q = q.Where(m => m.CreatedAt > c.At || (m.CreatedAt == c.At && string.Compare(m.MessageId, id) > 0));
            }
            else q = q.Where(m => m.CreatedAt > c.At);
        }

        var rows = await q.OrderBy(m => m.CreatedAt).ThenBy(m => m.MessageId).Take(take)
            .Select(m => new { m.MessageId, m.CreatedAt, m.SenderUserId, m.SenderUsername, m.Content, m.ReplyToMessageId, m.ReplyToUsername })
            .ToListAsync(ct);

        return rows.Select(m => new TimelineItem(TimelineKinds.Chat, m.CreatedAt, m.MessageId, m.SenderUsername, m.Content,
            UserId: m.SenderUserId, ReplyToMessageId: m.ReplyToMessageId, ReplyToUsername: m.ReplyToUsername)).ToList();
    }

    /// <summary>Stable snake_case names for <see cref="ChatterEventKind"/> (part of the API surface).</summary>
    public static string EventKindName(ChatterEventKind kind) => kind switch
    {
        ChatterEventKind.Follow => "follow",
        ChatterEventKind.SubscriptionNew => "subscription_new",
        ChatterEventKind.SubscriptionRenewal => "subscription_renewal",
        ChatterEventKind.SubscriptionGift => "subscription_gift",
        ChatterEventKind.KicksGift => "kicks_gift",
        ChatterEventKind.RewardRedemption => "reward_redemption",
        ChatterEventKind.Ban => "ban",
        ChatterEventKind.Timeout => "timeout",
        ChatterEventKind.MessageDeleted => "message_deleted",
        ChatterEventKind.Unban => "unban",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
