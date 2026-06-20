using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick;
using TailoredApps.Integrations.Kick.Clips;
using TailoredApps.Integrations.Kick.Models;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Services;

/// <summary>A channel that is allowed to show clips on the public OBS page.</summary>
public record ClipChannel(Guid Id, string Slug, string Username);

/// <summary>
/// Backs the public OBS clips endpoints. Responsibilities:
///  * scope a slug to a managed, enabled, clips-display-enabled broadcaster,
///  * cache each channel's clip pool (bounding Cloudflare hits via the sidecar),
///  * resolve a clip id → upstream m3u8 URL for the segment proxy.
/// Singleton: it owns the in-memory cache and per-slug fetch locks.
/// </summary>
public class ClipsCatalogService
{
    private readonly IKickClipsClient _clips;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly KickGlobalDefaults _opts;
    private readonly ILogger<ClipsCatalogService> _log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public ClipsCatalogService(
        IKickClipsClient clips,
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        IOptions<KickGlobalDefaults> opts,
        ILogger<ClipsCatalogService> log)
    {
        _clips = clips;
        _scopeFactory = scopeFactory;
        _cache = cache;
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>Resolves a slug to a managed channel allowed to display clips, or null. Cached (incl. negatives).</summary>
    public async Task<ClipChannel?> ResolveChannelAsync(string slug, CancellationToken ct)
    {
        slug = Normalize(slug);
        if (slug.Length == 0) return null;

        var key = $"clips:scope:{slug}";
        if (_cache.TryGetValue(key, out ClipChannel? cached))
            return cached; // may legitimately be null (negative cache)

        // Singleton service → resolve the scoped DbContext through a scope.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
        var b = await db.Broadcasters.AsNoTracking()
            .Where(x => x.ChannelSlug == slug && x.IsEnabled && x.ClipsDisplayEnabled)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        var result = b is null ? null : new ClipChannel(b.Id, b.ChannelSlug, b.Username);
        _cache.Set(key, result, TimeSpan.FromSeconds(result is null ? 30 : 120));
        return result;
    }

    /// <summary>Newest-first clip pool for a slug, cached with a single-flight lock to spare Cloudflare.</summary>
    public async Task<IReadOnlyList<KickClip>> GetPoolAsync(string slug, CancellationToken ct)
    {
        slug = Normalize(slug);
        var key = $"clips:pool:{slug}";
        if (_cache.TryGetValue(key, out IReadOnlyList<KickClip>? cached) && cached is not null)
            return cached;

        var gate = _locks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null)
                return cached;

            var pool = await _clips.GetChannelClipsAsync(slug, _opts.ClipsMaxPages, ct);
            var ttl = TimeSpan.FromMinutes(Math.Max(1, _opts.ClipsCacheMinutes));
            _cache.Set(key, pool, ttl);

            // Index each clip's video URL so the segment proxy can resolve by id
            // (it never knows the slug). Outlive the pool a bit for in-flight playback.
            foreach (var c in pool)
                _cache.Set(VideoUrlKey(c.Id), c.VideoUrl, ttl + TimeSpan.FromMinutes(30));

            _log.LogInformation("Loaded {Count} clips for {Slug}", pool.Count, slug);
            return pool;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Resolves a clip id to its upstream m3u8 URL (cache → single-clip refetch fallback). Null if unknown.</summary>
    public async Task<string?> ResolveClipVideoUrlAsync(string clipId, CancellationToken ct)
    {
        clipId = (clipId ?? "").Trim();
        if (clipId.Length == 0) return null;

        if (_cache.TryGetValue(VideoUrlKey(clipId), out string? url) && !string.IsNullOrEmpty(url))
            return url;

        var clip = await _clips.GetClipAsync(clipId, ct);
        if (clip is null || string.IsNullOrEmpty(clip.VideoUrl)) return null;

        _cache.Set(VideoUrlKey(clipId), clip.VideoUrl, TimeSpan.FromMinutes(30));
        return clip.VideoUrl;
    }

    private static string VideoUrlKey(string clipId) => $"clips:url:{clipId}";
    private static string Normalize(string slug) => (slug ?? "").Trim().ToLowerInvariant();
}
