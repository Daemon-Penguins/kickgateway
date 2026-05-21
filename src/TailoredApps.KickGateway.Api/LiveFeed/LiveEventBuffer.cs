using System.Collections.Concurrent;

namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>
/// Singleton in-memory event tap. Per-client rolling buffer of the last
/// <see cref="Capacity"/> events + a fan-out notification mechanism for
/// Blazor circuits viewing that client's live feed.
///
/// Memory bound: O(N_clients × Capacity). Events drop off the tail as new
/// ones arrive. No persistence — for that, query the <c>ReceivedWebhook</c>
/// table.
/// </summary>
public sealed class LiveEventBuffer
{
    public const int Capacity = 10;

    private sealed class ClientState
    {
        // Newest first. Lock for the few-item list mutations.
        public readonly LinkedList<LiveEvent> Events = new();
        public readonly object Sync = new();
        // Subscribers registered by Blazor circuits. Snapshot on publish so we
        // don't hold the lock while invoking callbacks.
        public readonly HashSet<Action<LiveEvent>> Subscribers = new();
    }

    private readonly ConcurrentDictionary<Guid, ClientState> _states = new();

    public void Append(LiveEvent ev)
    {
        var s = _states.GetOrAdd(ev.ClientAppId, _ => new ClientState());
        Action<LiveEvent>[] subs;
        lock (s.Sync)
        {
            s.Events.AddFirst(ev);
            while (s.Events.Count > Capacity) s.Events.RemoveLast();
            subs = s.Subscribers.ToArray();
        }
        foreach (var cb in subs)
        {
            try { cb(ev); } catch { /* one bad subscriber shouldn't break the others */ }
        }
    }

    public IReadOnlyList<LiveEvent> Snapshot(Guid clientAppId)
    {
        if (!_states.TryGetValue(clientAppId, out var s)) return Array.Empty<LiveEvent>();
        lock (s.Sync) return s.Events.ToArray();
    }

    /// <summary>
    /// Subscribe to live events for one client. Returns an <see cref="IDisposable"/>
    /// to unregister — Blazor pages call this in OnInitializedAsync and dispose
    /// in DisposeAsync.
    /// </summary>
    public IDisposable Subscribe(Guid clientAppId, Action<LiveEvent> onEvent)
    {
        var s = _states.GetOrAdd(clientAppId, _ => new ClientState());
        lock (s.Sync) s.Subscribers.Add(onEvent);
        return new Unsubscriber(s, onEvent);
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly ClientState _state;
        private readonly Action<LiveEvent> _cb;
        public Unsubscriber(ClientState s, Action<LiveEvent> cb) { _state = s; _cb = cb; }
        public void Dispose()
        {
            lock (_state.Sync) _state.Subscribers.Remove(_cb);
        }
    }
}
