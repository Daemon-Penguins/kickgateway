using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Twitch;
using TailoredApps.Integrations.Twitch.Auth;
using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.Integrations.Twitch.Playback;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Telemetry;

namespace TailoredApps.TwitchGateway.Capture;

/// <summary>
/// Every <see cref="TwitchCaptureOptions.LiveCheckSeconds"/>: read the channels with CaptureAudio on, find
/// which are live, and run one <see cref="TwitchAudioCaptureLoop"/> per live channel (cancelled when the
/// channel is removed or goes offline). Liveness comes from Helix <c>GET /streams</c> with the app token
/// when a developer application is configured, otherwise from the usher playlist itself (404 = offline) -
/// so the audio path works with no Twitch credentials at all, independently of the chat listener.
/// </summary>
public sealed class TwitchCaptureService : BackgroundService
{
    private sealed record Managed(string Login, string? UserId, string DisplayName);

    private sealed class Running
    {
        public required CancellationTokenSource Cts { get; init; }
        public required Task Task { get; init; }
        public required string StreamId { get; init; }
    }

    private readonly IServiceScopeFactory _scopes;
    private readonly ITwitchAuthClient _auth;
    private readonly ITwitchHelixClient _helix;
    private readonly ITwitchPlaybackClient _playback;
    private readonly TwitchAudioCaptureLoop _loop;
    private readonly TwitchCaptureOptions _opts;
    private readonly TwitchOptions _twitch;
    private readonly ILogger<TwitchCaptureService> _log;
    private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);

    public TwitchCaptureService(IServiceScopeFactory scopes, ITwitchAuthClient auth, ITwitchHelixClient helix, ITwitchPlaybackClient playback,
        TwitchAudioCaptureLoop loop, IOptions<TwitchCaptureOptions> opts, IOptions<TwitchOptions> twitch, ILogger<TwitchCaptureService> log)
    {
        _scopes = scopes;
        _auth = auth;
        _helix = helix;
        _playback = playback;
        _loop = loop;
        _opts = opts.Value;
        _twitch = twitch.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("Twitch audio capture is OFF (Twitch:Capture:Enabled=false) - idling");
            return;
        }
        TwitchGatewayMetrics.SetLiveCapturesProvider(() => _running.Count);
        _log.LogInformation("Twitch audio capture starting (rendition {Rendition}, live check every {Sec}s via {Source}{Filter})",
            _opts.Rendition, _opts.LiveCheckSeconds, _twitch.IsConfigured ? "Helix" : "usher probing",
            _opts.ChannelList.Length > 0 ? $", channels: {string.Join(",", _opts.ChannelList)}" : "");

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Twitch capture tick failed"); }
            await DelayAsync(TimeSpan.FromSeconds(Math.Max(10, _opts.LiveCheckSeconds)), ct);
        }

        foreach (var r in _running.Values) r.Cts.Cancel();
        await Task.WhenAll(_running.Values.Select(r => r.Task.ContinueWith(_ => { }, TaskScheduler.Default)));
        TwitchGatewayMetrics.SetLiveCapturesProvider(null);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var channels = await RosterAsync(ct);

        // Reap loops that ended on their own (offline / idle) so they can be restarted below.
        foreach (var (login, r) in _running.ToArray())
            if (r.Task.IsCompleted) _running.TryRemove(login, out _);

        // Stop loops for channels no longer wanted.
        foreach (var login in _running.Keys.Where(l => channels.All(c => c.Login != l)).ToList())
            if (_running.TryRemove(login, out var r))
            {
                _log.LogInformation("[{Login}] removed from the roster - stopping capture", login);
                r.Cts.Cancel();
            }

        var live = await LiveAsync(channels, ct);
        foreach (var (channel, streamId) in live)
        {
            if (_running.ContainsKey(channel.Login)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = Task.Run(async () =>
            {
                try { await _loop.RunAsync(channel.Login, channel.UserId ?? "", streamId, cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.LogWarning(ex, "[{Login}] capture loop crashed", channel.Login); }
            }, CancellationToken.None);
            _running[channel.Login] = new Running { Cts = cts, Task = task, StreamId = streamId };
        }
    }

    private async Task<List<Managed>> RosterAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
        var rows = await db.TwitchChannels.AsNoTracking().Where(c => c.IsEnabled && c.CaptureAudio).OrderBy(c => c.Login).ToListAsync(ct);
        var only = _opts.ChannelList;
        return rows
            .Where(r => only.Length == 0 || only.Contains(r.Login.ToLowerInvariant()))
            .Select(r => new Managed(r.Login.ToLowerInvariant(), r.UserId, string.IsNullOrWhiteSpace(r.DisplayName) ? r.Login : r.DisplayName!))
            .ToList();
    }

    /// <summary>(channel, stream id) for the channels that are live right now. Stream id is empty when only usher could tell.</summary>
    private async Task<List<(Managed Channel, string StreamId)>> LiveAsync(List<Managed> channels, CancellationToken ct)
    {
        var result = new List<(Managed, string)>();
        if (channels.Count == 0) return result;

        var token = _twitch.IsConfigured ? await _auth.GetAppAccessTokenAsync(ct) : null;
        if (token is not null)
        {
            var withIds = channels.Where(c => !string.IsNullOrEmpty(c.UserId)).ToList();
            var withoutIds = channels.Where(c => string.IsNullOrEmpty(c.UserId)).ToList();
            if (withoutIds.Count > 0)
            {
                // Logins added but never verified: resolve by login here (the id is also what the segments carry).
                var users = await _helix.GetUsersByLoginAsync(token, withoutIds.Select(c => c.Login), ct);
                foreach (var u in users)
                    withIds.Add(new Managed(u.Login.ToLowerInvariant(), u.Id, u.DisplayName));
            }
            try
            {
                var streams = await _helix.GetStreamsAsync(token, withIds.Select(c => c.UserId!), ct);
                foreach (var s in streams)
                {
                    var c = withIds.FirstOrDefault(x => x.UserId == s.UserId);
                    if (c is not null) result.Add((c, s.Id));
                }
                return result;
            }
            catch (TwitchUnauthorizedException)
            {
                _log.LogWarning("Helix refused the app token; falling back to usher probing this tick");
            }
        }

        // No developer app: ask usher per channel (a 404 master playlist = offline). One token + one playlist per channel per tick.
        foreach (var c in channels)
        {
            if (_running.ContainsKey(c.Login)) { result.Add((c, _running[c.Login].StreamId)); continue; }
            var access = await _playback.GetLivePlaybackAccessAsync(c.Login, ct);
            if (access is null) continue;
            var master = await _playback.GetPlaylistAsync(_playback.BuildMasterPlaybackProbe(c.Login, access), ct);
            if (master.IsSuccess && TwitchHls.IsMaster(master.Body)) result.Add((c, ""));
        }
        return result;
    }

    private static async Task DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); } catch (OperationCanceledException) { }
    }
}

file static class PlaybackProbeExtensions
{
    public static Uri BuildMasterPlaybackProbe(this ITwitchPlaybackClient playback, string login, TwitchPlaybackAccess access) =>
        playback.BuildMasterPlaylistUri(login, access);
}
