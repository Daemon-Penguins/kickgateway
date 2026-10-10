namespace TailoredApps.KickGateway.Subtitles.Relay;

/// <summary>
/// The token-gated delayed player: <c>/{slug}/player</c> (page), <c>/{slug}/hls/status</c>,
/// <c>/{slug}/hls/playlist.m3u8</c>, <c>/{slug}/hls/seg/{n}.ts|m4s</c>, <c>/{slug}/hls/init.mp4</c>.
/// Every route answers 404 without a valid <c>?token=</c>, so the relay is invisible to the public
/// site's visitors. The playlist carries a client id (<c>?cid=</c>) that counts as one viewer.
/// </summary>
public static class RelayEndpoints
{
    public static void MapRelay(this WebApplication app, string wwwroot)
    {
        app.MapGet("/{slug}/player", (string slug, HttpRequest req, SubtitlesOptions site, RelayOptions relay) =>
            Authorized(slug, req, site, relay, out _)
                ? Results.File(Path.Combine(wwwroot, "player.html"), "text/html; charset=utf-8")
                : Results.NotFound());

        app.MapGet("/{slug}/hls/status", (string slug, HttpRequest req, SubtitlesOptions site, RelayOptions relay, VideoRelay video) =>
        {
            if (!Authorized(slug, req, site, relay, out var s) || !video.Accepts(s)) return Results.NotFound();
            var stats = video.Stats(s);
            return Results.Json(new
            {
                slug = s,
                ready = stats.Segments > 0 && stats.BufferedSeconds >= Math.Min(relay.DefaultDelaySeconds + 4, relay.BufferSeconds),
                stats.Segments,
                stats.BufferedSeconds,
                bufferedMegabytes = stats.BufferedBytes / 1048576.0,
                stats.Viewers,
                maxViewers = relay.MaxViewersPerChannel,
                defaultDelaySeconds = relay.DefaultDelaySeconds,
                maxDelaySeconds = relay.MaxDelaySeconds,
                servedMegabytes = stats.ServedBytes / 1048576.0,
            }, Sse.Json);
        });

        app.MapGet("/{slug}/hls/playlist.m3u8", (string slug, HttpRequest req, string? cid, SubtitlesOptions site, RelayOptions relay, VideoRelay video) =>
        {
            if (!Authorized(slug, req, site, relay, out var s) || !video.Accepts(s)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(cid) || cid.Length > 64) return Results.BadRequest("cid required");
            if (!video.TouchViewer(s, cid))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            var suffix = "?token=" + Uri.EscapeDataString(req.Query["token"].ToString()) + "&cid=" + Uri.EscapeDataString(cid);
            var playlist = video.Playlist(s, suffix);
            if (playlist is null) return Results.NotFound();
            return Results.Text(playlist, "application/vnd.apple.mpegurl", System.Text.Encoding.UTF8);
        });

        app.MapGet("/{slug}/hls/seg/{file}", (string slug, string file, HttpRequest req, SubtitlesOptions site, RelayOptions relay, VideoRelay video) =>
        {
            if (!Authorized(slug, req, site, relay, out var s)) return Results.NotFound();
            var dot = file.IndexOf('.');
            if (dot <= 0 || !long.TryParse(file.AsSpan(0, dot), out var seq)) return Results.NotFound();
            var segment = video.Segment(s, seq);
            if (segment is null) return Results.NotFound();
            video.CountServed(s, segment.Data.Length);
            return Results.Bytes(segment.Data, segment.ContentType);
        });

        app.MapGet("/{slug}/hls/init.mp4", (string slug, HttpRequest req, SubtitlesOptions site, RelayOptions relay, VideoRelay video) =>
        {
            if (!Authorized(slug, req, site, relay, out var s)) return Results.NotFound();
            var init = video.Init(s);
            if (init is null) return Results.NotFound();
            video.CountServed(s, init.Data.Length);
            return Results.Bytes(init.Data, "video/mp4");
        });
    }

    private static bool Authorized(string rawSlug, HttpRequest req, SubtitlesOptions site, RelayOptions relay, out string slug)
    {
        slug = "";
        if (!relay.Enabled) return false;
        if (!SubtitlesOptions.TryNormalizeSlug(rawSlug, out slug) || !site.Allows(slug)) return false;
        var token = req.Query["token"].FirstOrDefault() ?? req.Headers["X-Relay-Token"].FirstOrDefault();
        return relay.TokenMatches(token);
    }
}
