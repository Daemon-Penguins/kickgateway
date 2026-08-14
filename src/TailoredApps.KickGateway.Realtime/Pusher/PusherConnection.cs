using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TailoredApps.KickGateway.Realtime.Pusher;

/// <summary>One decoded Pusher frame. <see cref="Data"/> is the inner payload text (for app events, itself JSON).</summary>
public record PusherFrame(string Event, string? Channel, string Data);

/// <summary>
/// A single resilient Pusher WebSocket connection. Owns its own reconnect loop, keepalive
/// pings, and the set of channels it's subscribed to; on any drop it reconnects (backoff +
/// jitter) and re-subscribes its whole set. App events (non <c>pusher*</c>) are handed to the
/// supplied callback; Pusher protocol frames (ping/pong/error/subscription) are handled here.
/// </summary>
public sealed class PusherConnection : IAsyncDisposable
{
    private readonly Uri _uri;
    private readonly RealtimeOptions _opts;
    private readonly ILogger _log;
    private readonly Func<PusherFrame, Task> _onAppEvent;
    private readonly int _id;

    private readonly object _gate = new();
    private readonly HashSet<string> _channels = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _pingIntervalSeconds;

    public PusherConnection(int id, Uri uri, RealtimeOptions opts, ILogger log, Func<PusherFrame, Task> onAppEvent)
    {
        _id = id;
        _uri = uri;
        _opts = opts;
        _log = log;
        _onAppEvent = onAppEvent;
        _pingIntervalSeconds = opts.PingIntervalSeconds;
    }

    public int Count { get { lock (_gate) return _channels.Count; } }
    public bool HasRoom => Count < _opts.MaxChannelsPerConnection;
    public bool Contains(string channel) { lock (_gate) return _channels.Contains(channel); }
    private bool IsOpen => _ws?.State == WebSocketState.Open;

    public void Start(CancellationToken outer)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task AddChannelAsync(string channel)
    {
        bool added;
        lock (_gate) added = _channels.Add(channel);
        if (added && IsOpen) await SendSubscribeAsync(channel, _cts!.Token);
    }

    public async Task RemoveChannelAsync(string channel)
    {
        bool removed;
        lock (_gate) removed = _channels.Remove(channel);
        if (removed && IsOpen) await SendUnsubscribeAsync(channel, _cts!.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = _opts.ReconnectMinSeconds;
        while (!ct.IsCancellationRequested)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await ws.ConnectAsync(_uri, ct);
                _ws = ws;
                _log.LogInformation("Pusher conn #{Id} connected ({Count} channels)", _id, Count);

                string[] snapshot;
                lock (_gate) snapshot = _channels.ToArray();
                foreach (var ch in snapshot) await SendSubscribeAsync(ch, ct);

                backoff = _opts.ReconnectMinSeconds; // reset after a clean connect
                var heartbeat = HeartbeatAsync(session.Token);
                await ReceiveLoopAsync(ws, session.Token);
                session.Cancel();
                await SwallowAsync(heartbeat);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Pusher conn #{Id} dropped; reconnecting in {Backoff}s", _id, backoff);
            }
            finally
            {
                _ws = null;
                session.Cancel();
            }

            if (ct.IsCancellationRequested) break;
            await DelayAsync(TimeSpan.FromSeconds(backoff) + Jitter(), ct);
            backoff = Math.Min(_opts.ReconnectMaxSeconds, Math.Max(1, backoff) * 2);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(ws, ct);
            if (text is null) return; // server closed
            await HandleAsync(text, ct);
        }
    }

    private async Task HandleAsync(string text, CancellationToken ct)
    {
        PusherFrame? frame;
        try { frame = Parse(text); }
        catch (Exception ex) { _log.LogDebug(ex, "Unparseable Pusher frame: {Text}", Truncate(text, 200)); return; }
        if (frame is null) return;

        var evt = frame.Event;
        switch (evt)
        {
            case "pusher:ping":
                await SendRawAsync("{\"event\":\"pusher:pong\",\"data\":\"{}\"}", ct);
                return;
            case "pusher:pong":
                return;
            case "pusher:connection_established":
                OnEstablished(frame.Data);
                return;
            case "pusher:error":
                _log.LogWarning("Pusher conn #{Id} error: {Data}", _id, Truncate(frame.Data, 300));
                throw new IOException("pusher:error → reconnect"); // bubble up to reconnect w/ backoff
        }

        if (evt.StartsWith("pusher_internal:", StringComparison.Ordinal))
        {
            _log.LogDebug("Pusher conn #{Id} {Event} {Channel}", _id, evt, frame.Channel);
            return;
        }
        if (evt.StartsWith("pusher:", StringComparison.Ordinal)) return;

        // App event — hand off for mapping/publishing.
        await _onAppEvent(frame);
    }

    private void OnEstablished(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var socketId = root.TryGetProperty("socket_id", out var s) ? s.GetString() : null;
            if (root.TryGetProperty("activity_timeout", out var at) && at.TryGetInt32(out var timeout) && timeout > 30)
                _pingIntervalSeconds = Math.Min(_opts.PingIntervalSeconds, timeout - 20);
            _log.LogInformation("Pusher conn #{Id} established (socket {Socket}, ping every {Ping}s)", _id, socketId, _pingIntervalSeconds);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not parse connection_established data");
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, _pingIntervalSeconds)), ct);
                if (IsOpen) await SendRawAsync("{\"event\":\"pusher:ping\",\"data\":\"{}\"}", ct);
            }
        }
        catch (OperationCanceledException) { /* session ended */ }
        catch (Exception ex) { _log.LogDebug(ex, "Heartbeat stopped"); }
    }

    // Channel names are gateway-controlled ("chatrooms.<id>.v2" / "channel.<id>"), so plain
    // quoting is safe (no user-supplied characters that would need JSON escaping).
    private Task SendSubscribeAsync(string channel, CancellationToken ct) =>
        SendRawAsync($"{{\"event\":\"pusher:subscribe\",\"data\":{{\"auth\":\"\",\"channel\":\"{channel}\"}}}}", ct);

    private Task SendUnsubscribeAsync(string channel, CancellationToken ct) =>
        SendRawAsync($"{{\"event\":\"pusher:unsubscribe\",\"data\":{{\"channel\":\"{channel}\"}}}}", ct);

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally { _sendLock.Release(); }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var res = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return null;
                }
                ms.Write(buffer, 0, res.Count);
                if (res.EndOfMessage) break;
                if (ms.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Pusher frame exceeded 4MB");
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>Parses the outer Pusher envelope. <c>data</c> arrives as a JSON string (double-encoded) — we return its decoded text.</summary>
    public static PusherFrame? Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.String)
            return null;

        string? channel = root.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String ? ch.GetString() : null;

        var data = "";
        if (root.TryGetProperty("data", out var d))
            data = d.ValueKind == JsonValueKind.String ? (d.GetString() ?? "") : d.GetRawText();

        return new PusherFrame(ev.GetString() ?? "", channel, data);
    }

    private static TimeSpan Jitter() => TimeSpan.FromMilliseconds(Random.Shared.Next(0, 750));

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    private static async Task SwallowAsync(Task t)
    {
        try { await t; } catch { /* ignore */ }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop is not null) await SwallowAsync(_loop);
        _cts?.Dispose();
        _sendLock.Dispose();
    }
}
