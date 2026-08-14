using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick.Channels;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Realtime;

/// <summary>
/// A managed channel the listener should follow, with its resolved Pusher ids and the set of
/// Pusher channels to subscribe (per-event capture flags).
/// </summary>
public record ManagedChannel(
    string Slug,
    string ChannelId,
    string? ChatroomId,
    bool CaptureChat = true,
    bool CaptureChannel = true,
    bool VideoCaptureEnabled = false);

/// <summary>
/// Builds the listener roster from the shared SQL DB (the single source of truth) and resolves
/// each slug's Kick channel_id + chatroom_id via the Cloudflare-bypass sidecar
/// (<see cref="IKickChannelClient"/>). The ids are stable, so they're cached per slug.
///
/// The roster is the UNION of two sources:
/// <list type="bullet">
/// <item>explicit <see cref="RealtimeChannel"/> rows (enabled) — managed at <c>/admin/realtime</c>,
/// slug-only, with per-event capture toggles;</item>
/// <item>enabled webhook broadcasters — which implicitly capture both chat + channel (video per
/// their own flag), so a channel you already OAuth-connected is followed without re-adding.</item>
/// </list>
/// When a slug appears in both, a given capture is enabled if EITHER source enables it.
/// </summary>
public class RosterProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IKickChannelClient _channels;
    private readonly ILogger<RosterProvider> _log;
    private readonly ConcurrentDictionary<string, (string ChannelId, string? ChatroomId)> _idCache = new();

    public RosterProvider(IServiceScopeFactory scopes, IKickChannelClient channels, ILogger<RosterProvider> log)
    {
        _scopes = scopes;
        _channels = channels;
        _log = log;
    }

    private readonly record struct Caps(bool Chat, bool Channel, bool Video);

    /// <summary>Managed channels (distinct by slug) with resolvable ids. Slugs whose ids can't be resolved yet are skipped this round.</summary>
    public async Task<IReadOnlyList<ManagedChannel>> GetManagedAsync(CancellationToken ct)
    {
        var wanted = new Dictionary<string, Caps>();

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();

            // Webhook broadcasters implicitly capture both Pusher channels; video per their flag.
            var broadcasters = await db.Broadcasters
                .Where(b => b.IsEnabled && b.KickClientApp!.IsEnabled)
                .Select(b => new { b.ChannelSlug, b.VideoCaptureEnabled })
                .ToListAsync(ct);
            foreach (var b in broadcasters)
                Merge(wanted, b.ChannelSlug, chat: true, channel: true, video: b.VideoCaptureEnabled);

            // Explicit realtime channels — per-event toggles chosen in the admin panel.
            var channels = await db.RealtimeChannels
                .Where(c => c.IsEnabled)
                .Select(c => new { c.Slug, c.CaptureChat, c.CaptureChannel, c.VideoCaptureEnabled })
                .ToListAsync(ct);
            foreach (var c in channels)
                Merge(wanted, c.Slug, chat: c.CaptureChat, channel: c.CaptureChannel, video: c.VideoCaptureEnabled);
        }

        var result = new List<ManagedChannel>(wanted.Count);
        foreach (var (slug, caps) in wanted)
        {
            // Nothing to do for a channel with every capture off (e.g. an enabled row with all
            // toggles cleared) — skip the id resolution it doesn't need.
            if (!caps.Chat && !caps.Channel && !caps.Video) continue;

            var ids = await ResolveIdsAsync(slug, ct);
            if (ids is null)
            {
                _log.LogDebug("Could not resolve channel/chatroom ids for {Slug} yet — will retry next roster refresh", slug);
                continue;
            }
            result.Add(new ManagedChannel(slug, ids.Value.ChannelId, ids.Value.ChatroomId, caps.Chat, caps.Channel, caps.Video));
        }
        return result;
    }

    // Union-merge one slug into the desired set: a capture is wanted if ANY contributing row wants it.
    private static void Merge(Dictionary<string, Caps> map, string? slug, bool chat, bool channel, bool video)
    {
        if (string.IsNullOrWhiteSpace(slug)) return;
        var key = slug.Trim().ToLowerInvariant();
        var cur = map.TryGetValue(key, out var existing) ? existing : default;
        map[key] = new Caps(cur.Chat || chat, cur.Channel || channel, cur.Video || video);
    }

    private async Task<(string ChannelId, string? ChatroomId)?> ResolveIdsAsync(string slug, CancellationToken ct)
    {
        if (_idCache.TryGetValue(slug, out var cached)) return cached;
        try
        {
            var info = await _channels.GetChannelAsync(slug, ct);
            if (info is null || string.IsNullOrEmpty(info.ChannelId)) return null;
            var tuple = (info.ChannelId, info.ChatroomId);
            _idCache[slug] = tuple;
            return tuple;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Channel id resolution threw for {Slug}", slug);
            return null;
        }
    }
}
