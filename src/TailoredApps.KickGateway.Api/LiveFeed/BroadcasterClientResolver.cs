using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>
/// Maps <c>BroadcasterAccountId → KickClientAppId</c> with a memory cache.
/// Used by the live-feed consumer to scope events to a client without doing a
/// DB lookup on every webhook. Cache is lazy + populated on miss; a row that's
/// later deleted will keep the stale id but that's harmless for the feed
/// (the row would no longer be visible in the UI anyway).
/// </summary>
public sealed class BroadcasterClientResolver
{
    private readonly ConcurrentDictionary<Guid, Guid> _cache = new();
    private readonly IServiceScopeFactory _scopes;

    public BroadcasterClientResolver(IServiceScopeFactory scopes) { _scopes = scopes; }

    public async ValueTask<Guid?> GetClientAppIdAsync(Guid broadcasterAccountId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(broadcasterAccountId, out var hit)) return hit;

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
        var id = await db.Broadcasters
            .AsNoTracking()
            .Where(x => x.Id == broadcasterAccountId)
            .Select(x => (Guid?)x.KickClientAppId)
            .FirstOrDefaultAsync(ct);
        if (id is null) return null;
        _cache[broadcasterAccountId] = id.Value;
        return id;
    }
}
