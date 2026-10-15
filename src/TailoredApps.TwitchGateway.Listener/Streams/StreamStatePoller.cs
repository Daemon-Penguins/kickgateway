using System.Text.Json;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.TwitchGateway.Listener.Ingest;
using TailoredApps.TwitchGateway.Listener.Mapping;
using TailoredApps.TwitchGateway.Listener.Roster;

namespace TailoredApps.TwitchGateway.Listener.Streams;

/// <summary>
/// Polls Helix <c>GET /streams</c> for every enabled channel and turns changes into synthetic events
/// (<c>TwitchStreamOnline</c> / <c>TwitchChannelUpdated</c> / <c>TwitchStreamOffline</c>) through the same
/// inbox + outbox as chat. Dedupe keys are built from the stream id, so a restart re-reporting a
/// stream that is still live is absorbed by the inbox, and a stream that started while the listener was
/// down is reported late rather than never. Offline is only reported for a stream this process saw live.
/// </summary>
public sealed class StreamStatePoller : BackgroundService
{
    private sealed record LiveState(string StreamId, DateTime StartedAt, string Fingerprint);

    private readonly TwitchRosterProvider _roster;
    private readonly TwitchAccessTokenSource _tokens;
    private readonly ITwitchHelixClient _helix;
    private readonly TwitchIngestQueue _queue;
    private readonly TwitchListenerOptions _opts;
    private readonly ILogger<StreamStatePoller> _log;
    private readonly Dictionary<string, LiveState> _live = new(StringComparer.Ordinal);

    public StreamStatePoller(TwitchRosterProvider roster, TwitchAccessTokenSource tokens, ITwitchHelixClient helix, TwitchIngestQueue queue,
        IOptions<TwitchListenerOptions> opts, ILogger<StreamStatePoller> log)
    {
        _roster = roster;
        _tokens = tokens;
        _helix = helix;
        _queue = queue;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_opts.StreamPollSeconds <= 0)
        {
            _log.LogInformation("Stream-state polling is off (Twitch:Listener:StreamPollSeconds=0)");
            return;
        }
        await DelayAsync(TimeSpan.FromSeconds(5), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Stream-state poll failed; retrying next tick"); }
            await DelayAsync(TimeSpan.FromSeconds(Math.Max(10, _opts.StreamPollSeconds)), ct);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var channels = await _roster.GetManagedAsync(ct);
        if (channels.Count == 0) return;
        var token = await _tokens.GetAsync(ct);
        if (token is null) return;

        IReadOnlyList<TwitchStream> streams;
        try
        {
            streams = await _helix.GetStreamsAsync(token, channels.Select(c => c.UserId), ct);
        }
        catch (TwitchUnauthorizedException)
        {
            await _tokens.MarkUnauthorizedAsync(ct);
            return;
        }

        var now = DateTime.UtcNow;
        var byUser = channels.ToDictionary(c => c.UserId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in streams)
        {
            if (!byUser.TryGetValue(s.UserId, out var channel)) continue;
            seen.Add(s.UserId);
            var fingerprint = TwitchEventMapper.Fingerprint(s.Title, s.GameId, s.Language);
            var raw = JsonSerializer.Serialize(s);

            if (!_live.TryGetValue(s.UserId, out var prev) || prev.StreamId != s.Id)
            {
                if (prev is not null) Enqueue(s.UserId, TwitchEventMapper.StreamOffline(prev.StreamId, prev.StartedAt, channel, now), now);
                Enqueue(s.UserId, TwitchEventMapper.StreamOnline(s, channel, now, raw), now);
                _live[s.UserId] = new LiveState(s.Id, s.StartedAt, fingerprint);
                continue;
            }
            if (prev.Fingerprint != fingerprint)
            {
                Enqueue(s.UserId, TwitchEventMapper.ChannelUpdated(s, channel, now, raw), now);
                _live[s.UserId] = prev with { Fingerprint = fingerprint };
            }
        }

        foreach (var userId in _live.Keys.Where(u => !seen.Contains(u)).ToList())
        {
            var prev = _live[userId];
            _live.Remove(userId);
            if (byUser.TryGetValue(userId, out var channel))
                Enqueue(userId, TwitchEventMapper.StreamOffline(prev.StreamId, prev.StartedAt, channel, now), now);
        }
    }

    private void Enqueue(string userId, Contracts.ITwitchEvent evt, DateTime now) =>
        _queue.TryEnqueue(new PendingTwitchEvent(userId, evt, now));

    private static async Task DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); } catch (OperationCanceledException) { }
    }
}
