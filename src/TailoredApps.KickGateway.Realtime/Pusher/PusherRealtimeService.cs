using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Realtime.Ingest;
using TailoredApps.KickGateway.Realtime.Mapping;

namespace TailoredApps.KickGateway.Realtime.Pusher;

/// <summary>
/// The listener core. On a timer it reads the managed roster and reconciles the set of Pusher
/// channels (<c>chatrooms.{id}.v2</c> + <c>channel.{id}</c> per broadcaster) across one or more
/// <see cref="PusherConnection"/>s (sharded by <see cref="RealtimeOptions.MaxChannelsPerConnection"/>).
/// Each incoming app-event frame is mapped to a typed contract and recorded+published via the
/// inbox+outbox.
/// </summary>
public class PusherRealtimeService : BackgroundService
{
    private readonly RosterProvider _roster;
    private readonly RealtimeFrameMapper _mapper;
    private readonly RealtimeIngestService _ingest;
    private readonly RealtimeOptions _opts;
    private readonly ILogger<PusherRealtimeService> _log;
    private readonly ILoggerFactory _loggerFactory;

    private readonly List<PusherConnection> _connections = new();
    // pusher channel name → owning broadcaster. Read from receive-loop threads, mutated by the
    // reconcile loop → concurrent.
    private readonly ConcurrentDictionary<string, ManagedChannel> _channelMeta = new();
    private CancellationToken _stopping;

    public PusherRealtimeService(RosterProvider roster, RealtimeFrameMapper mapper, RealtimeIngestService ingest,
        IOptions<RealtimeOptions> opts, ILogger<PusherRealtimeService> log, ILoggerFactory loggerFactory)
    {
        _roster = roster;
        _mapper = mapper;
        _ingest = ingest;
        _opts = opts.Value;
        _log = log;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _log.LogInformation("Realtime listener starting (host {Host}, appKey {Key}…)", _opts.WsHost, Preview(_opts.AppKey));

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Roster reconcile failed"); }
            await DelayAsync(TimeSpan.FromSeconds(_opts.RosterRefreshSeconds), stoppingToken);
        }

        foreach (var c in _connections) await c.DisposeAsync();
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var managed = await _roster.GetManagedAsync(ct);

        var desired = new Dictionary<string, ManagedChannel>();
        foreach (var m in managed)
        {
            if (m.CaptureChat && !string.IsNullOrEmpty(m.ChatroomId)) desired[$"chatrooms.{m.ChatroomId}.v2"] = m;
            if (m.CaptureChannel && !string.IsNullOrEmpty(m.ChannelId)) desired[$"channel.{m.ChannelId}"] = m;
        }

        // Drop channels no longer wanted.
        var toRemove = _channelMeta.Keys.Where(k => !desired.ContainsKey(k)).ToList();
        foreach (var ch in toRemove)
        {
            var conn = _connections.FirstOrDefault(c => c.Contains(ch));
            if (conn is not null) await conn.RemoveChannelAsync(ch);
            _channelMeta.TryRemove(ch, out _);
        }

        // Add newly-wanted channels.
        var toAdd = desired.Keys.Where(k => !_channelMeta.ContainsKey(k)).ToList();
        foreach (var ch in toAdd)
        {
            var conn = GetOrCreateConnectionWithRoom();
            _channelMeta[ch] = desired[ch];
            await conn.AddChannelAsync(ch);
        }

        // Refresh meta for existing channels (slug/video flag may change; ids are stable).
        foreach (var kv in desired) _channelMeta[kv.Key] = kv.Value;

        if (toAdd.Count > 0 || toRemove.Count > 0)
            _log.LogInformation("Reconciled subscriptions: +{Add} −{Remove} → {Total} channel(s) on {Conns} connection(s)",
                toAdd.Count, toRemove.Count, _channelMeta.Count, _connections.Count);
    }

    private PusherConnection GetOrCreateConnectionWithRoom()
    {
        var existing = _connections.FirstOrDefault(c => c.HasRoom);
        if (existing is not null) return existing;

        var id = _connections.Count + 1;
        var uri = new Uri($"wss://{_opts.WsHost}/app/{_opts.AppKey}?protocol={_opts.ProtocolVersion}&client=dotnet&version={_opts.ClientVersion}&flash=false");
        var conn = new PusherConnection(id, uri, _opts, _loggerFactory.CreateLogger($"Pusher.Conn{id}"), OnAppEventAsync);
        conn.Start(_stopping);
        _connections.Add(conn);
        return conn;
    }

    private async Task OnAppEventAsync(PusherFrame frame)
    {
        if (frame.Channel is null || !_channelMeta.TryGetValue(frame.Channel, out var meta)) return;
        try
        {
            var evt = _mapper.Map(frame, meta, DateTime.UtcNow);
            await _ingest.IngestAsync(evt, _stopping);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to ingest realtime frame {Event} on {Channel}", frame.Event, frame.Channel);
        }
    }

    private static string Preview(string key) => key.Length <= 6 ? key : key[..6];

    private static async Task DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); } catch (OperationCanceledException) { }
    }
}
