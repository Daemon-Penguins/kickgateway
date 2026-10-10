using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subtitles;

/// <summary>
/// In-memory fan-out of transcripts to connected viewers, per channel slug. Each slug keeps a short
/// backlog (so a viewer who just opened the page, or whose EventSource reconnected, gets the recent
/// lines) and a list of live subscribers, each with its own bounded mailbox — a stalled browser
/// loses its oldest lines instead of slowing everyone down. Ids are per slug and monotonic; they are
/// the SSE event ids, so <c>Last-Event-ID</c> resumes exactly where the connection dropped.
/// A translation that arrives later is merged into the backlog item and pushed to live subscribers
/// as a <see cref="FeedMessage.Translation"/> carrying the updated event under the same id.
/// </summary>
public sealed class TranscriptFeed
{
    private const int MailboxCapacity = 64;

    private sealed class Subscriber
    {
        public readonly Channel<FeedMessage> Mailbox = Channel.CreateBounded<FeedMessage>(
            new BoundedChannelOptions(MailboxCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    }

    private sealed class ChannelState
    {
        public long NextId = 1;
        public readonly LinkedList<SubtitleEvent> Backlog = new();
        public readonly List<Subscriber> Subscribers = new();
    }

    private readonly Dictionary<string, ChannelState> _channels = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeSpan _backlogAge;
    private readonly int _backlogItems;
    private readonly TimeProvider _time;

    public TranscriptFeed(SubtitlesOptions options, TimeProvider? time = null)
    {
        _backlogAge = TimeSpan.FromSeconds(options.BacklogSeconds);
        _backlogItems = options.MaxBacklogItems;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Slugs that currently have a backlog or viewers.</summary>
    public IReadOnlyList<string> ActiveSlugs
    {
        get { lock (_gate) return _channels.Where(kv => kv.Value.Backlog.Count > 0 || kv.Value.Subscribers.Count > 0).Select(kv => kv.Key).OrderBy(s => s).ToArray(); }
    }

    public int SubscriberCount(string slug)
    {
        lock (_gate) return _channels.TryGetValue(slug, out var s) ? s.Subscribers.Count : 0;
    }

    /// <summary>Stores the transcript in its channel's backlog and hands it to every live subscriber. Returns the event as published, or null when the slug is unusable.</summary>
    public SubtitleEvent? Publish(LiveTranscript transcript)
    {
        if (!SubtitlesOptions.TryNormalizeSlug(transcript.BroadcasterSlug, out var slug)) return null;
        if (string.IsNullOrWhiteSpace(transcript.Text)) return null;

        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            var state = Get(slug);
            var ev = SubtitleEvent.From(transcript, slug, state.NextId++, now);
            state.Backlog.AddLast(ev);
            Trim(state, now);
            Notify(state, new FeedMessage(FeedMessage.Transcript, ev));
            return ev;
        }
    }

    /// <summary>
    /// Attaches a translation to the transcript it refers to (matched on slug + start + audio-clock
    /// position). Returns the updated event, or null when the transcript is unknown or already gone from
    /// the backlog — a late translation has nothing to show on.
    /// </summary>
    public SubtitleEvent? AttachTranslation(LiveTranscriptTranslation translation)
    {
        if (!SubtitlesOptions.TryNormalizeSlug(translation.BroadcasterSlug, out var slug)) return null;
        var language = (translation.TargetLanguage ?? "").Trim().ToLowerInvariant();
        if (language.Length == 0 || string.IsNullOrWhiteSpace(translation.Text)) return null;

        lock (_gate)
        {
            if (!_channels.TryGetValue(slug, out var state)) return null;
            for (var node = state.Backlog.Last; node is not null; node = node.Previous)
            {
                if (!node.Value.Matches(translation.StartedAt, translation.AudioStartSeconds)) continue;
                var updated = node.Value.WithTranslation(language, translation.Text.Trim(), translation.Segments);
                node.Value = updated;
                Notify(state, new FeedMessage(FeedMessage.Translation, updated));
                return updated;
            }
            return null;
        }
    }

    /// <summary>Backlog items of <paramref name="slug"/> with an id above <paramref name="afterId"/>, oldest first.</summary>
    public IReadOnlyList<SubtitleEvent> Backlog(string slug, long afterId = 0)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(slug, out var state)) return [];
            Trim(state, _time.GetUtcNow().UtcDateTime);
            return state.Backlog.Where(e => e.Id > afterId).ToArray();
        }
    }

    /// <summary>
    /// Backlog after <paramref name="afterId"/> followed by live messages, gap-free: the snapshot and the
    /// subscription happen under one lock, so nothing published in between is missed or duplicated.
    /// </summary>
    public async IAsyncEnumerable<FeedMessage> SubscribeAsync(string slug, long afterId, [EnumeratorCancellation] CancellationToken ct)
    {
        var subscriber = new Subscriber();
        SubtitleEvent[] backlog;
        lock (_gate)
        {
            var state = Get(slug);
            Trim(state, _time.GetUtcNow().UtcDateTime);
            backlog = state.Backlog.Where(e => e.Id > afterId).ToArray();
            state.Subscribers.Add(subscriber);
        }

        try
        {
            foreach (var ev in backlog) yield return new FeedMessage(FeedMessage.Transcript, ev);
            await foreach (var message in subscriber.Mailbox.Reader.ReadAllAsync(ct))
                yield return message;
        }
        finally
        {
            lock (_gate)
            {
                if (_channels.TryGetValue(slug, out var state))
                {
                    state.Subscribers.Remove(subscriber);
                    if (state.Subscribers.Count == 0 && state.Backlog.Count == 0) _channels.Remove(slug);
                }
            }
        }
    }

    private ChannelState Get(string slug)
    {
        if (!_channels.TryGetValue(slug, out var state))
            _channels[slug] = state = new ChannelState();
        return state;
    }

    private static void Notify(ChannelState state, FeedMessage message)
    {
        foreach (var sub in state.Subscribers)
            sub.Mailbox.Writer.TryWrite(message); // bounded + DropOldest: never blocks the consumer
    }

    private void Trim(ChannelState state, DateTime now)
    {
        while (state.Backlog.Count > _backlogItems
               || (state.Backlog.First is { } first && now - first.Value.ReceivedAt > _backlogAge))
            state.Backlog.RemoveFirst();
    }
}
