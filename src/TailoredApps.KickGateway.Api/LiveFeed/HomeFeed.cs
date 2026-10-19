using System.Text.RegularExpressions;
using TailoredApps.KickGateway.Contracts.Realtime;
using TailoredApps.TwitchGateway.Contracts.Chat;

namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>What a <see cref="HomeFeedItem"/> is: a chat line or a slice of transcribed speech.</summary>
public enum HomeFeedKind
{
    Chat = 1,
    Transcript = 2,
}

/// <summary>
/// What a consumer hands to <see cref="HomeFeed.Append"/>: one chat message or transcript slice,
/// already reduced to what the landing page shows (<c>{username}{badges}: {text}</c>).
/// </summary>
/// <param name="Kind">Chat line or speech.</param>
/// <param name="Platform"><c>kick</c> or <c>twitch</c> (<see cref="Data.ChatPlatforms"/>).</param>
/// <param name="Channel">Broadcaster slug (Kick) / login (Twitch), lowercase.</param>
/// <param name="Username">Who said it: the chatter, or the channel itself for speech.</param>
/// <param name="Badges">Short badge labels (<see cref="ChatBadgeLabels"/>) - or the language code for speech.</param>
/// <param name="Text">Message / transcript text as published (the page trims it for display).</param>
/// <param name="Color">Chatter's username colour as the platform sent it (<c>#rrggbb</c>), null when none.</param>
/// <param name="DedupeKey">
/// Stable key that collapses the same message seen twice (a Kick chat line arrives via the webhook AND the Pusher stream
/// for channels followed on both; two client apps on one channel). Empty = never deduped.
/// </param>
public sealed record HomeFeedEntry(
    HomeFeedKind Kind,
    string Platform,
    string Channel,
    string Username,
    string[] Badges,
    string Text,
    string? Color,
    string DedupeKey);

/// <summary>A <see cref="HomeFeedEntry"/> the feed accepted, with its sequence number and arrival time.</summary>
public sealed record HomeFeedItem(long Id, DateTime At, HomeFeedEntry Entry);

/// <summary>Messages and transcript slices the feed saw during one UTC minute.</summary>
public sealed record MinuteLoad(DateTime Minute, int Messages, int Transcripts);

/// <summary>
/// Singleton in-memory tap behind the public landing page (<c>/</c>): the last <see cref="Capacity"/> chat
/// messages / transcript slices across every platform and channel, a fan-out to the Blazor circuits showing
/// them, and per-minute counters for the "load" chart. Fed by the Api's own bus taps
/// (<see cref="LiveFeedConsumer"/>, <see cref="RealtimeLiveFeedConsumer"/>, <see cref="TwitchLiveFeedConsumer"/>)
/// and the transcript consumers after a successful store.
/// <para>
/// Best-effort and per process: nothing is persisted, counters start empty on every restart, and with several
/// Api replicas each one sees its own taps (the chat taps are per-replica queues, so every replica sees all chat;
/// transcripts land on a shared queue, so a replica only sees the slices it stored itself).
/// </para>
/// </summary>
public sealed class HomeFeed
{
    /// <summary>How many items the feed keeps (and the page shows).</summary>
    public const int Capacity = 10;

    /// <summary>Per-minute counters are kept this long; the page asks for the last 8.</summary>
    public const int BucketRetentionMinutes = 60;

    /// <summary>How many dedupe keys are remembered. A duplicate delivery lands within seconds, so this is generous.</summary>
    public const int DedupeCapacity = 4096;

    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly LinkedList<HomeFeedItem> _items = new();          // newest first
    private readonly HashSet<Action<HomeFeedItem>> _subscribers = new();
    private readonly Dictionary<long, Bucket> _buckets = new();        // key = minute ticks (UTC)
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private long _nextId;

    private sealed class Bucket
    {
        public int Messages;
        public int Transcripts;
    }

    public HomeFeed() : this(TimeProvider.System) { }

    public HomeFeed(TimeProvider clock) { _clock = clock; }

    /// <summary>Dedupe key for a chat message: platform + the platform's message id.</summary>
    public static string ChatKey(string platform, string messageId) =>
        string.IsNullOrWhiteSpace(messageId) ? "" : $"{platform}:{messageId.Trim()}";

    /// <summary>
    /// Accept an entry: it becomes the newest item, is counted in the current minute and is pushed to every
    /// subscriber. Returns null when <see cref="HomeFeedEntry.DedupeKey"/> was already seen (nothing is counted
    /// or pushed then).
    /// </summary>
    public HomeFeedItem? Append(HomeFeedEntry entry)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        HomeFeedItem item;
        Action<HomeFeedItem>[] subscribers;
        lock (_sync)
        {
            if (entry.DedupeKey.Length > 0)
            {
                if (!_seen.Add(entry.DedupeKey)) return null;
                _seenOrder.Enqueue(entry.DedupeKey);
                while (_seenOrder.Count > DedupeCapacity) _seen.Remove(_seenOrder.Dequeue());
            }

            item = new HomeFeedItem(++_nextId, now, entry);
            _items.AddFirst(item);
            while (_items.Count > Capacity) _items.RemoveLast();

            var key = MinuteFloor(now).Ticks;
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                Prune(now);
                _buckets[key] = bucket = new Bucket();
            }
            if (entry.Kind == HomeFeedKind.Transcript) bucket.Transcripts++;
            else bucket.Messages++;

            subscribers = _subscribers.ToArray();
        }

        foreach (var cb in subscribers)
        {
            try { cb(item); } catch { /* one bad circuit must not break the others */ }
        }
        return item;
    }

    /// <summary>The kept items, newest first.</summary>
    public IReadOnlyList<HomeFeedItem> Snapshot()
    {
        lock (_sync) return _items.ToArray();
    }

    /// <summary>
    /// Per-minute counts for the last <paramref name="minutes"/> UTC minutes, oldest first, ending with the minute
    /// that is still running. Minutes without traffic are present with zeros.
    /// </summary>
    public IReadOnlyList<MinuteLoad> Load(int minutes)
    {
        if (minutes <= 0) return Array.Empty<MinuteLoad>();
        var current = MinuteFloor(_clock.GetUtcNow().UtcDateTime);
        var result = new MinuteLoad[minutes];
        lock (_sync)
        {
            for (var i = 0; i < minutes; i++)
            {
                var minute = current.AddMinutes(i - (minutes - 1));
                var b = _buckets.GetValueOrDefault(minute.Ticks);
                result[i] = new MinuteLoad(minute, b?.Messages ?? 0, b?.Transcripts ?? 0);
            }
        }
        return result;
    }

    /// <summary>
    /// Register for new items. Blazor pages subscribe in <c>OnInitializedAsync</c> and dispose the returned
    /// handle in <c>DisposeAsync</c>; the callback runs on the publishing (MassTransit) thread.
    /// </summary>
    public IDisposable Subscribe(Action<HomeFeedItem> onItem)
    {
        lock (_sync) _subscribers.Add(onItem);
        return new Unsubscriber(this, onItem);
    }

    private void Prune(DateTime now)
    {
        var oldest = MinuteFloor(now).AddMinutes(-BucketRetentionMinutes).Ticks;
        foreach (var key in _buckets.Keys.Where(k => k < oldest).ToList()) _buckets.Remove(key);
    }

    internal static DateTime MinuteFloor(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private sealed class Unsubscriber(HomeFeed feed, Action<HomeFeedItem> cb) : IDisposable
    {
        public void Dispose()
        {
            lock (feed._sync) feed._subscribers.Remove(cb);
        }
    }
}

/// <summary>
/// Short, platform-neutral badge labels for the landing page. Kick webhooks carry badge <i>texts</i>
/// ("Moderator"), the Pusher stream badge <i>types</i> ("moderator", with a count for subscriber months),
/// Twitch EventSub badge <i>set ids</i> ("moderator", "subscriber" with months in <c>info</c>) - all land on the same
/// handful of labels (<c>mod</c>, <c>sub 14</c>, <c>vip</c>, ...). Unknown badges keep their (sanitised) name.
/// </summary>
public static partial class ChatBadgeLabels
{
    public const int MaxBadges = 4;
    private const int MaxLabelLength = 16;

    /// <summary>Kick webhook badges (<c>ChatMessageSent.SenderBadges</c>): texts or types, no counts.</summary>
    public static string[] FromKick(IEnumerable<string>? badges) =>
        Collect((badges ?? []).Select(b => Short(b, 0)));

    /// <summary>Kick realtime badges (<c>RealtimeChatMessage.SenderBadges</c>): type + count.</summary>
    public static string[] FromKick(IEnumerable<RealtimeChatBadge>? badges) =>
        Collect((badges ?? []).Select(b => Short(b.Type.Length > 0 ? b.Type : b.Text, b.Count)));

    /// <summary>Twitch EventSub badges: set id + months in <c>info</c> for subscribers.</summary>
    public static string[] FromTwitch(IEnumerable<TwitchChatBadge>? badges) =>
        Collect((badges ?? []).Select(b => Short(b.SetId, int.TryParse(b.Info, out var n) ? n : 0)));

    /// <summary>One badge name (any platform's spelling) to its short label; empty when the name is blank.</summary>
    public static string Short(string? name, int count)
    {
        var key = Normalize(name);
        if (key.Length == 0) return "";
        return key switch
        {
            "moderator" or "mod" => "mod",
            "subscriber" or "sub" => count > 0 ? $"sub {count}" : "sub",
            "sub_gifter" or "subgifter" or "sub_gift_leader" => "gifter",
            "broadcaster" or "host" => "broadcaster",
            "verified" => "verified",
            "premium" => "prime",
            "bits" or "bits_leader" or "bits_charity" => "bits",
            "artist_badge" => "artist",
            _ => key.Length > MaxLabelLength ? key[..MaxLabelLength] : key,
        };
    }

    private static string[] Collect(IEnumerable<string> labels) =>
        labels.Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxBadges).ToArray();

    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return NotBadgeChars().Replace(s, "");
    }

    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex NotBadgeChars();
}

/// <summary>Display helpers for feed text (pure, so the page stays markup).</summary>
public static partial class HomeFeedText
{
    /// <summary>Longest message / transcript shown on the page.</summary>
    public const int MaxLength = 220;

    /// <summary>
    /// Kick inlines emotes as <c>[emote:12345:KEKW]</c>; the page has no emote images, so show <c>:KEKW:</c>.
    /// Whitespace is collapsed and the result cut to <see cref="MaxLength"/> with an ellipsis.
    /// </summary>
    public static string Display(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = KickEmote().Replace(text, ":$1:");
        s = Whitespace().Replace(s, " ").Trim();
        return s.Length <= MaxLength ? s : s[..(MaxLength - 1)] + "…";
    }

    /// <summary>A username colour is used as inline CSS only when it is a plain <c>#rgb</c>/<c>#rrggbb</c>.</summary>
    public static string? CssColor(string? color) =>
        color is not null && HexColor().IsMatch(color) ? color : null;

    [GeneratedRegex(@"\[emote:\d+:([^\]\s]+)\]")]
    private static partial Regex KickEmote();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();
}
