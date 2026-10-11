using System.Globalization;
using System.Text;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>
/// One channel's chronology: what the stream SAID (<c>LiveTranscripts</c>), what happened
/// (<c>ChatterEvents</c>) and what chat WROTE (<c>ChatMessages</c>), merged by stream time. The pure
/// parts (kinds, keyset cursor, merge) live here so they can be unit-tested; <see cref="TimelineQueries"/>
/// does the SQL.
/// </summary>
public static class TimelineKinds
{
    public const string Speech = "speech";
    public const string Event = "event";
    public const string Chat = "chat";

    /// <summary>All kinds, in rank order.</summary>
    public static readonly IReadOnlyList<string> All = [Speech, Event, Chat];

    /// <summary>
    /// Order at equal timestamps: a slice that starts at t is read before the chat written at t, and an
    /// event (sub, gift, deletion) before the messages of the same second. The rank is part of the keyset
    /// cursor, so it must never change for an existing kind.
    /// </summary>
    public static int Rank(string kind) => kind switch
    {
        Speech => 0,
        Event => 1,
        Chat => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown timeline kind"),
    };

    /// <summary>
    /// Parses the <c>include</c> query parameter: a comma-separated subset of <c>speech</c>, <c>chat</c>,
    /// <c>events</c> (a few synonyms accepted); empty = all three.
    /// </summary>
    public static bool TryParseInclude(string? include, out IReadOnlySet<string> kinds, out string? error)
    {
        error = null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        kinds = set;
        if (string.IsNullOrWhiteSpace(include))
        {
            set.UnionWith(All);
            return true;
        }
        foreach (var raw in include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kind = raw.ToLowerInvariant() switch
            {
                Speech or "transcript" or "transcripts" => Speech,
                Event or "events" => Event,
                Chat or "message" or "messages" => Chat,
                _ => null,
            };
            if (kind is null)
            {
                error = $"invalid 'include': {raw} (use a comma-separated subset of speech,chat,events)";
                return false;
            }
            set.Add(kind);
        }
        return true;
    }
}

/// <summary>
/// One row of a channel timeline. <see cref="Kind"/> says which optional fields are present:
/// <c>speech</c> (<see cref="EndedAt"/>, <see cref="Language"/>, <see cref="Confidence"/>, <see cref="Translations"/>),
/// <c>chat</c> (<see cref="UserId"/>, <see cref="ReplyToMessageId"/>, <see cref="ReplyToUsername"/>) or
/// <c>event</c> (<see cref="UserId"/>, <see cref="EventKind"/>, <see cref="Counterpart"/>, <see cref="Amount"/>).
/// <see cref="Who"/> is the channel slug for speech (the stream's audio: streamer plus surroundings) and the
/// username otherwise; <see cref="Text"/> is the transcript, the message or the event detail.
/// </summary>
public sealed record TimelineItem(
    string Kind,
    DateTime At,
    string Id,
    string Who,
    string Text,
    string? UserId = null,
    DateTime? EndedAt = null,
    string? Language = null,
    float? Confidence = null,
    IReadOnlyDictionary<string, string>? Translations = null,
    string? ReplyToMessageId = null,
    string? ReplyToUsername = null,
    string? EventKind = null,
    string? Counterpart = null,
    int? Amount = null);

/// <summary>A page of a channel timeline, oldest first. Pass <see cref="NextCursor"/> back as <c>cursor</c> for the next page.</summary>
public sealed record TimelinePage(
    string Channel,
    AnalyticsWindow Window,
    IReadOnlyList<string> Kinds,
    float MinConfidence,
    IReadOnlyList<TimelineItem> Items,
    string? NextCursor,
    string Chronology);

/// <summary>
/// Keyset position of a timeline page: the (at, rank, id) of the last item served. Each kind is read with its
/// own predicate derived from it (see <see cref="TimelineQueries"/>), which makes paging exact even when many
/// rows share a timestamp (chat has second precision). Opaque to clients.
/// </summary>
public readonly record struct TimelineCursor(DateTime At, int Rank, string Id)
{
    public static TimelineCursor Of(TimelineItem item) => new(item.At, TimelineKinds.Rank(item.Kind), item.Id);

    /// <summary>Numeric id for a speech cursor (<c>LiveTranscripts.Id</c>); <see cref="TryParse"/> guarantees it parses.</summary>
    public long SpeechId => long.Parse(Id, NumberStyles.None, CultureInfo.InvariantCulture);

    public string Format()
    {
        var raw = $"{At.Ticks.ToString(CultureInfo.InvariantCulture)}|{Rank.ToString(CultureInfo.InvariantCulture)}|{Id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryParse(string? cursor, out TimelineCursor result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        string raw;
        try
        {
            var b64 = cursor.Trim().Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return false;
        }
        var parts = raw.Split('|', 3);
        if (parts.Length != 3) return false;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rank)) return false;
        if (rank < 0 || rank > TimelineKinds.Rank(TimelineKinds.Chat)) return false;
        if (parts[2].Length == 0) return false;
        if (rank == TimelineKinds.Rank(TimelineKinds.Speech) && !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
        result = new TimelineCursor(new DateTime(ticks, DateTimeKind.Utc), rank, parts[2]);
        return true;
    }
}

/// <summary>Merges the per-kind result sets of one page.</summary>
public static class TimelineMerge
{
    /// <summary>
    /// K-way merge by (at, rank) of lists that each arrive in their SQL order (time asc, id asc) and keep it;
    /// two kinds never share a rank, so ids are never compared across kinds. Each source must have been read
    /// with <c>TOP limit+1</c>: a leftover in any source after taking <paramref name="limit"/> items means
    /// there is a next page.
    /// </summary>
    public static (IReadOnlyList<TimelineItem> Items, string? NextCursor) Page(IReadOnlyList<IReadOnlyList<TimelineItem>> sources, int limit)
    {
        var heads = new int[sources.Count];
        var items = new List<TimelineItem>();
        while (items.Count < limit)
        {
            var best = -1;
            for (var i = 0; i < sources.Count; i++)
            {
                if (heads[i] >= sources[i].Count) continue;
                if (best < 0 || Compare(sources[i][heads[i]], sources[best][heads[best]]) < 0) best = i;
            }
            if (best < 0) break;
            items.Add(sources[best][heads[best]++]);
        }

        var more = false;
        for (var i = 0; i < sources.Count && !more; i++) more = heads[i] < sources[i].Count;
        return (items, more && items.Count > 0 ? TimelineCursor.Of(items[^1]).Format() : null);
    }

    private static int Compare(TimelineItem a, TimelineItem b)
    {
        var c = a.At.CompareTo(b.At);
        return c != 0 ? c : TimelineKinds.Rank(a.Kind).CompareTo(TimelineKinds.Rank(b.Kind));
    }
}
