using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Twitch;
using TailoredApps.Integrations.Twitch.EventSub;
using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.KickGateway.Telemetry;
using TailoredApps.TwitchGateway.Listener.Ingest;
using TailoredApps.TwitchGateway.Listener.Mapping;
using TailoredApps.TwitchGateway.Listener.Roster;

namespace TailoredApps.TwitchGateway.Listener.EventSub;

/// <summary>
/// The listener core. On a timer it reads the roster and reconciles the chat subscriptions across up to
/// <see cref="TwitchListenerOptions.MaxSessions"/> <see cref="EventSubConnection"/>s (Twitch: 3 sessions
/// per user token, 300 subscriptions each). Each notification is mapped to a contract and handed to the
/// <see cref="TwitchIngestQueue"/>; the receive loops never wait on the database. At startup every
/// EventSub subscription of the bot is deleted: they belonged to sessions that no longer exist.
/// </summary>
public sealed class EventSubSessionManager : BackgroundService
{
    private readonly TwitchRosterProvider _roster;
    private readonly TwitchAccessTokenSource _tokens;
    private readonly ITwitchHelixClient _helix;
    private readonly TwitchIngestQueue _queue;
    private readonly IOptions<TwitchListenerOptions> _opts;
    private readonly IOptions<TwitchOptions> _twitch;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<EventSubSessionManager> _log;

    private readonly List<EventSubConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ManagedTwitchChannel> _channelsById = new(StringComparer.Ordinal);
    private CancellationToken _stopping;
    private bool _cleanedUp;

    public EventSubSessionManager(TwitchRosterProvider roster, TwitchAccessTokenSource tokens, ITwitchHelixClient helix, TwitchIngestQueue queue,
        IOptions<TwitchListenerOptions> opts, IOptions<TwitchOptions> twitch, ILoggerFactory loggerFactory)
    {
        _roster = roster;
        _tokens = tokens;
        _helix = helix;
        _queue = queue;
        _opts = opts;
        _twitch = twitch;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<EventSubSessionManager>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _queue.Start();
        TwitchGatewayMetrics.SetSubscriptionsProvider(() => _connections.Sum(c => c.SubscriptionCount));

        var o = _opts.Value;
        _log.LogInformation("Twitch listener starting (app {Configured}; chat types {Types}; ingest {Workers} worker(s) x {Capacity})",
            _twitch.Value.IsConfigured ? "configured" : "NOT configured - set Twitch:ClientId/ClientSecret",
            string.Join(",", o.EnabledChatTypes()), _queue.Workers, _queue.Capacity);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Twitch roster reconcile failed"); }
            await DelayAsync(TimeSpan.FromSeconds(Math.Max(5, o.RosterRefreshSeconds)), stoppingToken);
        }

        foreach (var c in _connections) await c.DisposeAsync();
        await _queue.DisposeAsync();
        TwitchGatewayMetrics.SetSubscriptionsProvider(null);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        if (!_cleanedUp) _cleanedUp = await CleanupStaleSubscriptionsAsync(ct);

        var managed = await _roster.GetManagedAsync(ct);
        var desired = managed.Where(m => m.CaptureChat).ToDictionary(m => m.UserId, StringComparer.Ordinal);
        foreach (var kv in desired) _channelsById[kv.Key] = kv.Value;
        foreach (var stale in _channelsById.Keys.Where(k => !desired.ContainsKey(k)).ToList()) _channelsById.TryRemove(stale, out _);

        var typesPerChannel = _opts.Value.EnabledChatTypes().Count;
        var removed = 0;
        var added = 0;

        foreach (var conn in _connections)
            foreach (var userId in conn.UserIds.Where(u => !desired.ContainsKey(u)))
            {
                await conn.RemoveChannelAsync(userId, ct);
                removed++;
            }

        foreach (var channel in desired.Values)
        {
            if (_connections.Any(c => c.Contains(channel.UserId))) continue;
            var conn = _connections.FirstOrDefault(c => c.HasRoom(typesPerChannel));
            if (conn is null)
            {
                if (_connections.Count >= Math.Max(1, Math.Min(_opts.Value.MaxSessions, EventSubProtocol.MaxConnectionsPerToken)))
                {
                    _log.LogWarning("EventSub capacity exhausted ({Sessions} session(s) x {Max} subscriptions) - '{Login}' is not followed", _connections.Count, _opts.Value.MaxSubscriptionsPerSession, channel.Login);
                    continue;
                }
                conn = new EventSubConnection(_connections.Count + 1, _opts, _twitch, _tokens, _helix, OnNotificationAsync, OnRevocationAsync,
                    _loggerFactory.CreateLogger($"EventSub.Conn{_connections.Count + 1}"));
                conn.Start(_stopping);
                _connections.Add(conn);
            }
            await conn.AddChannelAsync(channel, ct);
            added++;
        }

        if (added > 0 || removed > 0)
            _log.LogInformation("Reconciled Twitch subscriptions: +{Add} -{Remove} -> {Total} channel(s) on {Conns} session(s)", added, removed, desired.Count, _connections.Count);
    }

    /// <summary>Deletes every EventSub subscription of the bot: WebSocket subscriptions die with their session, so anything listed belongs to a previous process.</summary>
    private async Task<bool> CleanupStaleSubscriptionsAsync(CancellationToken ct)
    {
        var token = await _tokens.GetBotTokenAsync(ct);
        if (token is null) return false;
        try
        {
            var existing = await _helix.ListEventSubSubscriptionsAsync(token, null, ct);
            var deleted = 0;
            foreach (var sub in existing)
            {
                if (await _helix.DeleteEventSubSubscriptionAsync(token, sub.Id, ct)) deleted++;
            }
            if (existing.Count > 0) _log.LogInformation("Deleted {Deleted}/{Total} stale EventSub subscription(s) from previous sessions", deleted, existing.Count);
            return true;
        }
        catch (TwitchUnauthorizedException)
        {
            await _tokens.MarkUnauthorizedAsync(ct);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Listing stale EventSub subscriptions failed - will retry");
            return false;
        }
    }

    // Runs on a connection's receive loop: route + map + enqueue, never wait on the database.
    private Task OnNotificationAsync(EventSubMessage msg)
    {
        var receivedAt = DateTime.UtcNow;
        var broadcasterId = BroadcasterUserId(msg.EventJson);
        if (broadcasterId.Length == 0 || !_channelsById.TryGetValue(broadcasterId, out var channel))
        {
            _log.LogDebug("Notification {Type} for unknown broadcaster {Id} ignored", msg.SubscriptionType, broadcasterId);
            return Task.CompletedTask;
        }
        var evt = TwitchEventMapper.Map(msg, channel, receivedAt);
        _queue.TryEnqueue(new PendingTwitchEvent(broadcasterId, evt, receivedAt));
        return Task.CompletedTask;
    }

    private Task OnRevocationAsync(EventSubMessage msg) => Task.CompletedTask;

    /// <summary>Cheap scan for <c>broadcaster_user_id</c> without building the full document twice.</summary>
    private static string BroadcasterUserId(string? eventJson)
    {
        if (string.IsNullOrEmpty(eventJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(eventJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("broadcaster_user_id", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static async Task DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); } catch (OperationCanceledException) { }
    }
}
