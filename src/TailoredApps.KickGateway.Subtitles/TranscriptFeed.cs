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
/// </summary>
public sealed class TranscriptFeed
{
    private const int MailboxCapacity = 64;

    private sealed class Subscriber
    {
        public readonly Channel<SubtitleEvent> Mailbox = Channel.CreateBounded<SubtitleEvent>(
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
            foreach (var sub in state.Subscribers)
                sub.Mailbox.Writer.TryWrite(ev); // bounded + DropOldest: never blocks the consumer
            return ev;
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
    /// Backlog after <paramref name="afterId"/> followed by live events, gap-free: the snapshot and the
    /// subscription happen under one lock, so nothing published in between is missed or duplicated.
    /// </summary>
    public async IAsyncEnumerable<SubtitleEvent> SubscribeAsync(string slug, long afterId, [EnumeratorCancellation] CancellationToken ct)
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
            foreach (var ev in backlog) yield return ev;
            await foreach (var ev in subscriber.Mailbox.Reader.ReadAllAsync(ct))
                yield return ev;
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

    private void Trim(ChannelState state, DateTime now)
    {
        while (state.Backlog.Count > _backlogItems
               || (state.Backlog.First is { } first && now - first.Value.ReceivedAt > _backlogAge))
            state.Backlog.RemoveFirst();
    }
}
