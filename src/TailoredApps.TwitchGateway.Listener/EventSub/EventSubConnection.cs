using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Twitch;
using TailoredApps.Integrations.Twitch.EventSub;
using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.KickGateway.Telemetry;
using TailoredApps.TwitchGateway.Listener.Roster;

namespace TailoredApps.TwitchGateway.Listener.EventSub;

/// <summary>
/// One EventSub WebSocket session and the chat subscriptions it carries. Owns its reconnect loop: connect,
/// take the welcome, create every subscription for its channels through Helix (the first one within
/// Twitch's 10 s window), then read notifications until the socket dies or Twitch asks for a reconnect
/// (<c>session_reconnect</c>: the new socket is welcomed before the old one is closed and the
/// subscriptions carry over). A dead session means its subscriptions are gone too, so a plain
/// reconnect starts from scratch and re-creates them. Notifications are handed to a callback that must
/// not block (it enqueues).
/// </summary>
public sealed class EventSubConnection : IAsyncDisposable
{
    private static readonly TimeSpan WelcomeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepaliveGrace = TimeSpan.FromSeconds(10);

    private readonly int _id;
    private readonly TwitchListenerOptions _opts;
    private readonly TwitchOptions _twitch;
    private readonly TwitchAccessTokenSource _tokens;
    private readonly ITwitchHelixClient _helix;
    private readonly Func<EventSubMessage, Task> _onNotification;
    private readonly Func<EventSubMessage, Task> _onRevocation;
    private readonly ILogger _log;

    private readonly object _gate = new();
    private readonly Dictionary<string, ManagedTwitchChannel> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<(string UserId, string Type), string> _subscriptionIds = new();
    private readonly SemaphoreSlim _subscribeLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _sessionId;
    private volatile bool _established;
    private DateTime _noTokenWarnedAt = DateTime.MinValue;

    public EventSubConnection(int id, IOptions<TwitchListenerOptions> opts, IOptions<TwitchOptions> twitch, TwitchAccessTokenSource tokens,
        ITwitchHelixClient helix, Func<EventSubMessage, Task> onNotification, Func<EventSubMessage, Task> onRevocation, ILogger log)
    {
        _id = id;
        _opts = opts.Value;
        _twitch = twitch.Value;
        _tokens = tokens;
        _helix = helix;
        _onNotification = onNotification;
        _onRevocation = onRevocation;
        _log = log;
    }

    public int Id => _id;

    public int ChannelCount { get { lock (_gate) return _channels.Count; } }

    /// <summary>Enabled subscriptions on the current session (0 while reconnecting).</summary>
    public int SubscriptionCount { get { lock (_gate) return _established ? _subscriptionIds.Count : 0; } }

    public bool IsEstablished => _established;

    public bool Contains(string userId) { lock (_gate) return _channels.ContainsKey(userId); }

    public bool HasRoom(int typesPerChannel)
    {
        lock (_gate) return (_channels.Count + 1) * Math.Max(1, typesPerChannel) <= _opts.MaxSubscriptionsPerSession;
    }

    public IReadOnlyList<string> UserIds { get { lock (_gate) return _channels.Keys.ToList(); } }

    public void Start(CancellationToken outer)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task AddChannelAsync(ManagedTwitchChannel channel, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_channels.TryAdd(channel.UserId, channel)) return;
        }
        if (!_established) return; // subscribed on the next welcome
        var token = await _tokens.GetBotTokenAsync(ct);
        if (token is null) return;
        var sessionId = _sessionId;
        if (sessionId is null) return;
        await _subscribeLock.WaitAsync(ct);
        try { await SubscribeChannelAsync(token, sessionId, channel, ct); }
        catch (EventSubSessionException) { /* the read loop notices and reconnects */ }
        finally { _subscribeLock.Release(); }
    }

    public async Task RemoveChannelAsync(string userId, CancellationToken ct)
    {
        List<string> ids;
        lock (_gate)
        {
            if (!_channels.Remove(userId)) return;
            ids = _subscriptionIds.Where(kv => kv.Key.UserId == userId).Select(kv => kv.Value).ToList();
            foreach (var key in _subscriptionIds.Keys.Where(k => k.UserId == userId).ToList()) _subscriptionIds.Remove(key);
        }
        if (ids.Count == 0 || !_established) return;
        var token = await _tokens.GetBotTokenAsync(ct);
        if (token is null) return;
        foreach (var id in ids)
        {
            try { await _helix.DeleteEventSubSubscriptionAsync(token, id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "EventSub #{Id}: deleting subscription {Sub} failed", _id, id); }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = _opts.ReconnectMinSeconds;
        while (!ct.IsCancellationRequested)
        {
            _established = false;
            lock (_gate) _subscriptionIds.Clear();
            ClientWebSocket? ws = null;
            var refused = false;
            Exception? failure = null;
            try
            {
                var token = await _tokens.GetBotTokenAsync(ct);
                if (token is null)
                {
                    // No bot connected (or Twitch refuses it): nothing to subscribe with. Wait, do not hammer.
                    if (DateTime.UtcNow - _noTokenWarnedAt > TimeSpan.FromMinutes(5))
                    {
                        _log.LogWarning("EventSub #{Id}: no Twitch bot token - connect the bot at /admin/twitch (and set Twitch:ClientId/ClientSecret). Retrying every {Sec}s", _id, _opts.ReconnectMaxSeconds);
                        _noTokenWarnedAt = DateTime.UtcNow;
                    }
                    TwitchGatewayMetrics.RecordSessionEvent("no_token");
                    await DelayAsync(TimeSpan.FromSeconds(_opts.ReconnectMaxSeconds), ct);
                    continue;
                }

                var url = EventSubProtocol.WithKeepalive(_twitch.EventSubWebSocketUrl, _opts.KeepaliveTimeoutSeconds);
                EventSubMessage welcome;
                (ws, welcome) = await ConnectAsync(url, ct);
                _sessionId = welcome.Session!.Id;
                var keepalive = TimeSpan.FromSeconds(welcome.Session.KeepaliveTimeoutSeconds ?? _opts.KeepaliveTimeoutSeconds) + KeepaliveGrace;
                _log.LogInformation("EventSub #{Id}: session {Session} connected ({Channels} channel(s), keepalive {Keepalive}s)", _id, _sessionId, ChannelCount, keepalive.TotalSeconds);
                TwitchGatewayMetrics.RecordSessionEvent("connected");

                await _subscribeLock.WaitAsync(ct);
                try { await SubscribeAllAsync(token, _sessionId, ct); }
                finally { _subscribeLock.Release(); }
                _established = true;
                backoff = _opts.ReconnectMinSeconds;

                while (!ct.IsCancellationRequested)
                {
                    var text = await ReceiveTextAsync(ws, keepalive, ct);
                    if (text is null)
                    {
                        _log.LogInformation("EventSub #{Id}: Twitch closed the session ({Status} {Reason})", _id, ws.CloseStatus, ws.CloseStatusDescription);
                        TwitchGatewayMetrics.RecordSessionEvent("closed");
                        break;
                    }

                    EventSubMessage? msg;
                    try { msg = EventSubProtocol.Parse(text); }
                    catch (System.Text.Json.JsonException ex)
                    {
                        _log.LogDebug(ex, "EventSub #{Id}: unparseable frame: {Text}", _id, Truncate(text, 200));
                        continue;
                    }
                    if (msg is null) continue;

                    if (msg.IsKeepalive) continue;
                    if (msg.IsNotification)
                    {
                        await _onNotification(msg);
                        continue;
                    }
                    if (msg.IsReconnect && msg.Session?.ReconnectUrl is { Length: > 0 } reconnectUrl)
                    {
                        // Keep the old socket until the new one is welcomed; subscriptions carry over to the new session.
                        var (newWs, newWelcome) = await ConnectAsync(reconnectUrl, ct);
                        try { ws.Dispose(); } catch { /* old socket, done */ }
                        ws = newWs;
                        _sessionId = newWelcome.Session!.Id;
                        keepalive = TimeSpan.FromSeconds(newWelcome.Session.KeepaliveTimeoutSeconds ?? _opts.KeepaliveTimeoutSeconds) + KeepaliveGrace;
                        _log.LogInformation("EventSub #{Id}: moved to session {Session} on Twitch's request", _id, _sessionId);
                        TwitchGatewayMetrics.RecordSessionEvent("reconnect");
                        continue;
                    }
                    if (msg.IsRevocation)
                    {
                        TwitchGatewayMetrics.RecordSessionEvent("revoked");
                        lock (_gate)
                        {
                            var key = _subscriptionIds.FirstOrDefault(kv => kv.Value == msg.SubscriptionId).Key;
                            if (key.UserId is not null) _subscriptionIds.Remove(key);
                        }
                        _log.LogWarning("EventSub #{Id}: subscription {Sub} ({Type}) revoked: {Status}", _id, msg.SubscriptionId, msg.SubscriptionType, msg.SubscriptionStatus);
                        await _onRevocation(msg);
                        if (msg.SubscriptionStatus == "authorization_revoked") await _tokens.MarkUnauthorizedAsync(ct);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (EventSubUnauthorizedException ex)
            {
                refused = true;
                failure = ex;
                await _tokens.MarkUnauthorizedAsync(ct);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                _established = false;
                if (ws is not null)
                {
                    try
                    {
                        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                    }
                    catch { /* best effort */ }
                    ws.Dispose();
                }
            }

            if (ct.IsCancellationRequested) break;

            backoff = NextBackoff(backoff, _established || failure is null, refused, _opts.ReconnectMinSeconds, _opts.ReconnectMaxSeconds);
            if (failure is TimeoutException)
            {
                TwitchGatewayMetrics.RecordSessionEvent("keepalive_timeout");
                _log.LogWarning("EventSub #{Id}: no keepalive from Twitch - reconnecting in {Backoff}s", _id, backoff);
            }
            else if (refused)
            {
                TwitchGatewayMetrics.RecordSessionEvent("unauthorized");
                _log.LogError("EventSub #{Id}: Twitch rejected the bot token ({Message}); refreshing and retrying in {Backoff}s", _id, failure!.Message, backoff);
            }
            else if (failure is not null)
            {
                TwitchGatewayMetrics.RecordSessionEvent("error");
                _log.LogWarning(failure, "EventSub #{Id}: session dropped; reconnecting in {Backoff}s", _id, backoff);
            }
            await DelayAsync(TimeSpan.FromSeconds(backoff) + Jitter(), ct);
        }
    }

    /// <summary>Opens a socket and waits for the welcome (15 s). Anything else first is a protocol error.</summary>
    private async Task<(ClientWebSocket Socket, EventSubMessage Welcome)> ConnectAsync(string url, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await ws.ConnectAsync(new Uri(url), ct);
            var text = await ReceiveTextAsync(ws, WelcomeTimeout, ct) ?? throw new EventSubSessionException("socket closed before the welcome");
            var msg = EventSubProtocol.Parse(text);
            if (msg is null || !msg.IsWelcome || msg.Session is null || string.IsNullOrEmpty(msg.Session.Id))
                throw new EventSubSessionException("expected session_welcome, got: " + Truncate(text, 200));
            return (ws, msg);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private async Task SubscribeAllAsync(string token, string sessionId, CancellationToken ct)
    {
        List<ManagedTwitchChannel> snapshot;
        lock (_gate) snapshot = _channels.Values.ToList();
        foreach (var channel in snapshot)
        {
            // The subscribe window only applies to the first subscription; after it the session is "used".
            await SubscribeChannelAsync(token, sessionId, channel, ct);
        }
        _log.LogInformation("EventSub #{Id}: {Subs} subscription(s) on session {Session} for {Channels} channel(s)", _id, SubscriptionCountUnsafe(), sessionId, snapshot.Count);
    }

    private int SubscriptionCountUnsafe() { lock (_gate) return _subscriptionIds.Count; }

    private async Task SubscribeChannelAsync(string token, string sessionId, ManagedTwitchChannel channel, CancellationToken ct)
    {
        var identity = _tokens.Identity ?? throw new EventSubSessionException("bot identity unknown");
        foreach (var type in _opts.EnabledChatTypes())
        {
            lock (_gate)
            {
                if (!_channels.ContainsKey(channel.UserId)) return; // removed meanwhile
                if (_subscriptionIds.ContainsKey((channel.UserId, type))) continue;
            }
            var condition = EventSubTypes.ChatCondition(channel.UserId, identity.UserId);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await _helix.CreateEventSubSubscriptionAsync(token, type, "1", condition, sessionId, ct);
                if (result.Success && result.Subscription is not null)
                {
                    lock (_gate) _subscriptionIds[(channel.UserId, type)] = result.Subscription.Id;
                    break;
                }
                if (result.StatusCode == 401) throw new EventSubUnauthorizedException(result.Error ?? "401");
                if (result.StatusCode == 409)
                {
                    // Already exists on this session (we lost its id) - harmless, it delivers anyway.
                    break;
                }
                if (result.IsRateLimited && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                    continue;
                }
                if (result.StatusCode == 400 && (result.Error?.Contains("session", StringComparison.OrdinalIgnoreCase) ?? false))
                    throw new EventSubSessionException("Twitch no longer knows session " + sessionId + ": " + result.Error);

                TwitchGatewayMetrics.RecordSessionEvent("subscribe_failed");
                _log.LogWarning("EventSub #{Id}: subscribing {Type} for {Login} failed (HTTP {Status}): {Error}", _id, type, channel.Login, result.StatusCode, result.Error);
                break;
            }
        }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, TimeSpan timeout, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult res;
                try
                {
                    res = await ws.ReceiveAsync(buffer.AsMemory(), timer.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"no EventSub message within {timeout.TotalSeconds:F0}s");
                }
                if (res.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, res.Count);
                if (res.EndOfMessage) break;
                if (ms.Length > 4 * 1024 * 1024) throw new InvalidOperationException("EventSub frame exceeded 4MB");
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Same policy as the Kick listener: a refusal waits the maximum, an established session earns the minimum back, anything else doubles.</summary>
    public static int NextBackoff(int previous, bool established, bool refused, int min, int max)
    {
        min = Math.Max(1, min);
        max = Math.Max(min, max);
        if (refused) return max;
        if (established) return min;
        return Math.Min(max, Math.Max(min, previous) * 2);
    }

    private static TimeSpan Jitter() => TimeSpan.FromMilliseconds(Random.Shared.Next(0, 750));

    private static async Task DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); } catch (OperationCanceledException) { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop is not null)
        {
            try { await _loop; } catch { /* ignore */ }
        }
        _cts?.Dispose();
        _subscribeLock.Dispose();
    }
}

/// <summary>The session is unusable (closed before the welcome, Twitch forgot it); the loop reconnects.</summary>
public sealed class EventSubSessionException(string message) : Exception(message);

/// <summary>Helix answered 401 while subscribing: the bot token is dead until refreshed.</summary>
public sealed class EventSubUnauthorizedException(string message) : Exception(message);
