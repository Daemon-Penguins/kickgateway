using Microsoft.Extensions.DependencyInjection;
using TailoredApps.KickGateway.Api.Services;

namespace TailoredApps.KickGateway.Api.Endpoints;

/// <summary>
/// Public (anonymous) endpoints powering the OBS clips player:
///  * the static player page,
///  * the JSON playlist for a channel,
///  * an HLS manifest rewriter + segment proxy (the clips CDN sends no CORS header,
///    so playback must be same-origin).
/// Clip listings come via the Cloudflare-bypass sidecar (see IKickClipsClient); the
/// segment proxy talks to the CDN directly (it doesn't block .NET).
/// </summary>
public static class ObsClipsEndpoints
{
    public const string CdnHttpClientName = "KickClipsCdn";

    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public static IServiceCollection AddObsClips(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddHttpClient(CdnHttpClientName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUa));
        services.AddSingleton<ClipsCatalogService>();
        return services;
    }

    public static IEndpointRouteBuilder MapObsClipsEndpoints(this IEndpointRouteBuilder routes)
    {
        // 1) Player page — static HTML; its JS reads the slug from the path and calls the JSON API.
        routes.MapGet("/obs/clips/{slug}", (string slug, IWebHostEnvironment env) =>
        {
            var path = Path.Combine(env.WebRootPath ?? "wwwroot", "obs", "clips-player.html");
            return File.Exists(path)
                ? Results.File(path, "text/html; charset=utf-8")
                : Results.NotFound();
        }).AllowAnonymous();

        // 2) JSON playlist for a channel (newest-first; the player does latest-2-then-random).
        routes.MapGet("/api/obs/clips/{slug}", async (string slug, ClipsCatalogService catalog, CancellationToken ct) =>
        {
            var channel = await catalog.ResolveChannelAsync(slug, ct);
            if (channel is null)
                return Results.NotFound(new { error = "unknown or disabled channel" });

            var pool = await catalog.GetPoolAsync(channel.Slug, ct);
            var clips = pool.Select(c => new ObsClipDto(
                c.Id,
                c.Title,
                $"/api/obs/hls/{Uri.EscapeDataString(c.Id)}/playlist.m3u8",
                c.ThumbnailUrl,
                c.DurationSeconds,
                c.Views,
                c.CreatedAt,
                c.CreatorUsername,
                c.CategoryName)).ToList();

            return Results.Ok(new ObsPlaylistDto(new ObsChannelDto(channel.Slug, channel.Username), clips.Count, clips));
        }).AllowAnonymous();

        // 3) Rewritten HLS manifest — segment URIs point back at our proxy (4).
        routes.MapGet("/api/obs/hls/{clipId}/playlist.m3u8", async (
            string clipId, ClipsCatalogService catalog, IHttpClientFactory f, CancellationToken ct) =>
        {
            var videoUrl = await catalog.ResolveClipVideoUrlAsync(clipId, ct);
            if (string.IsNullOrEmpty(videoUrl) || !IsClipsCdn(videoUrl))
                return Results.NotFound();

            string manifest;
            try
            {
                manifest = await f.CreateClient(CdnHttpClientName).GetStringAsync(videoUrl, ct);
            }
            catch
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            var rewritten = HlsManifest.Rewrite(manifest, new Uri(videoUrl), $"/api/obs/hls/{Uri.EscapeDataString(clipId)}/seg");
            return Results.Text(rewritten, "application/vnd.apple.mpegurl");
        }).AllowAnonymous();

        // 4) Segment proxy — forwards Range, returns 206 + Content-Range (handles EXT-X-BYTERANGE).
        routes.MapGet("/api/obs/hls/{clipId}/seg", async (
            string clipId, string u, HttpContext ctx, IHttpClientFactory f, CancellationToken ct) =>
        {
            var upstream = HlsManifest.DecodeSegmentUrl(u);
            if (upstream is null || !IsClipsCdn(upstream))
                return Results.BadRequest("bad segment");

            using var req = new HttpRequestMessage(HttpMethod.Get, upstream);
            if (ctx.Request.Headers.TryGetValue("Range", out var range))
                req.Headers.TryAddWithoutValidation("Range", range.ToString());

            HttpResponseMessage resp;
            try
            {
                resp = await f.CreateClient(CdnHttpClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            using (resp)
            {
                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.Headers["Accept-Ranges"] = "bytes";
                ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
                if (resp.Content.Headers.ContentType is { } ctype)
                    ctx.Response.ContentType = ctype.ToString();
                if (resp.Content.Headers.ContentLength is { } len)
                    ctx.Response.ContentLength = len;
                if (resp.Content.Headers.TryGetValues("Content-Range", out var cr))
                    ctx.Response.Headers["Content-Range"] = string.Join(",", cr);

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                await stream.CopyToAsync(ctx.Response.Body, ct);
            }
            return Results.Empty;
        }).AllowAnonymous();

        return routes;
    }

    /// <summary>Guards the proxy against being used as a general relay — kick.com CDN only.</summary>
    private static bool IsClipsCdn(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals("clips.kick.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".kick.com", StringComparison.OrdinalIgnoreCase));

    private record ObsChannelDto(string Slug, string Username);

    private record ObsClipDto(
        string Id,
        string Title,
        string Src,
        string Thumbnail,
        int Duration,
        int Views,
        DateTime CreatedAt,
        string? Creator,
        string? Category);

    private record ObsPlaylistDto(ObsChannelDto Channel, int Count, IReadOnlyList<ObsClipDto> Clips);
}
