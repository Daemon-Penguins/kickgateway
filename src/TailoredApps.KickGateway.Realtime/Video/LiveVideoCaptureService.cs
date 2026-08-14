using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick.Channels;
using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.KickGateway.Realtime.Video;

/// <summary>
/// Supervises live-video capture. Off unless <c>Kick:Realtime:Video:Enabled</c> is set. When on,
/// it polls live state for every managed, capture-enabled channel and runs a
/// <see cref="HlsCaptureLoop"/> for each one that is live — starting when it goes live, stopping
/// when it goes offline. Continuous while live, exactly per config.
/// </summary>
public class LiveVideoCaptureService : BackgroundService
{
    private readonly RosterProvider _roster;
    private readonly IKickChannelClient _channels;
    private readonly HlsCaptureLoop _capture;
    private readonly RealtimeOptions _opts;
    private readonly ILogger<LiveVideoCaptureService> _log;

    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Task)> _active = new();
    private string _lastStatus = "";
    private readonly Dictionary<string, string> _slugState = new();

    public LiveVideoCaptureService(RosterProvider roster, IKickChannelClient channels, HlsCaptureLoop capture,
        IOptions<RealtimeOptions> opts, ILogger<LiveVideoCaptureService> log)
    {
        _roster = roster;
        _channels = channels;
        _capture = capture;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Video.Enabled)
        {
            _log.LogInformation("Live video capture disabled (Kick:Realtime:Video:Enabled=false)");
            return;
        }

        _log.LogInformation("Live video capture enabled (max {Bw}kbps, segment TTL {Ttl}s)",
            _opts.Video.MaxBitrateKbps, _opts.Video.SegmentTtlSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Video capture tick failed"); }
            await DelayAsync(TimeSpan.FromSeconds(_opts.Video.LiveCheckSeconds), stoppingToken);
        }

        foreach (var kv in _active) kv.Value.Cts.Cancel();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var roster = await _roster.GetManagedAsync(ct);
        var managed = roster.Where(m => m.VideoCaptureEnabled).ToList();
        var wanted = new HashSet<string>();

        // Roster summary, logged when it changes. Tells you immediately whether ANY channel is
        // eligible for video at all (the #1 "why do I see nothing" cause).
        var status = managed.Count == 0
            ? $"0 capture-enabled channels (of {roster.Count} in roster) — turn Video on for a channel in /admin/realtime"
            : $"{managed.Count} capture-enabled: {string.Join(", ", managed.Select(m => m.Slug))}";
        if (status != _lastStatus)
        {
            _log.LogInformation("Live video roster: {Status}", status);
            _lastStatus = status;
        }

        foreach (var m in managed)
        {
            KickChannelInfo? info = null;
            string state;
            try
            {
                info = await _channels.GetChannelAsync(m.Slug, ct);
                if (info is null) state = "live-check returned null (sidecar/Cloudflare issue?)";
                else if (!info.IsLive) state = "offline — not capturing";
                else if (string.IsNullOrEmpty(info.PlaybackUrl)) state = "LIVE but channel payload has no playback_url";
                else state = $"LIVE — playback host {new Uri(info.PlaybackUrl!).Host}";
            }
            catch (Exception ex)
            {
                state = "live-check threw: " + ex.Message;
            }

            // Per-channel state, logged only when it changes — so an offline→LIVE transition shows
            // up clearly (and you see WHY a live channel isn't capturing) without per-tick spam.
            if (!_slugState.TryGetValue(m.Slug, out var prev) || prev != state)
            {
                _log.LogInformation("Video channel {Slug}: {State}", m.Slug, state);
                _slugState[m.Slug] = state;
            }

            if (info is null || !info.IsLive || string.IsNullOrEmpty(info.PlaybackUrl)) continue;
            wanted.Add(m.Slug);

            if (_active.ContainsKey(m.Slug)) continue; // already capturing (or completed → reaped below, restarts next tick)

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var slug = m.Slug;
            var chanId = m.ChannelId;
            var url = info.PlaybackUrl!;
            var task = Task.Run(() => _capture.RunAsync(slug, chanId, url, cts.Token), cts.Token);
            _active[slug] = (cts, task);
            _log.LogInformation("Video capture started for {Slug} (host {Host})", slug, new Uri(url).Host);
        }

        // Stop captures whose channel went offline/disabled; reap any that ended on their own.
        foreach (var slug in _active.Keys.ToList())
        {
            var (cts, task) = _active[slug];
            if (!wanted.Contains(slug) || task.IsCompleted)
            {
                if (!wanted.Contains(slug))
                {
                    cts.Cancel();
                    _log.LogInformation("Video capture stopped for {Slug}", slug);
                }
                // Surface a capture loop that died on its own — otherwise the exception is swallowed.
                if (task.IsFaulted)
                    _log.LogWarning(task.Exception?.GetBaseException(), "Video capture for {Slug} ended with an error", slug);
                cts.Dispose();
                _active.Remove(slug);
            }
        }
    }

    private static async Task DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); } catch (OperationCanceledException) { }
    }
}
