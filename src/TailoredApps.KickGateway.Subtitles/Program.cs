using MassTransit;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subtitles;
using TailoredApps.KickGateway.Subtitles.Relay;

// Live subtitles site. Two ways to watch:
//  • GET /{slug} — public: Kick's own player (player.kick.com iframe; the video never touches this server)
//    with the channel's live transcripts overlaid, streamed to the page over Server-Sent Events from the
//    LiveTranscript exchange. Captions trail the picture by ~ChunkSeconds + inference − player buffer.
//  • GET /{slug}/player?token=…&delay=15 — token-gated relay: the gateway already pulls the live HLS for
//    transcription (LiveVideoSegment); this service keeps the last ~90 s per channel in memory and serves it
//    as a live playlist the browser's hls.js positions `delay` seconds behind live, so captions can be timed
//    exactly on #EXT-X-PROGRAM-DATE-TIME. Costs outbound transfer per viewer (bitrate × viewers), hence the
//    token, the viewer cap and the stats log. Off unless Subtitles:Relay:Token is set.
// Nothing is stored here — the Api owns persistence. Query flags on /{slug}: ?overlay=1 (captions only,
// transparent — an OBS browser source), ?muted=false.

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var opts = builder.Configuration.GetSection(SubtitlesOptions.Section).Get<SubtitlesOptions>() ?? new SubtitlesOptions();
opts.Validate();
var relayOpts = builder.Configuration.GetSection(RelayOptions.Section).Get<RelayOptions>() ?? new RelayOptions();
relayOpts.Validate();

builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(relayOpts);
builder.Services.AddSingleton<TranscriptFeed>();
builder.Services.AddSingleton<VideoRelay>();
if (relayOpts.Enabled) builder.Services.AddHostedService<RelayStatsLogger>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<LiveTranscriptConsumer>();
    if (relayOpts.Enabled) x.AddConsumer<LiveVideoSegmentConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        // Throwaway, per-instance queues: a subtitle or a video segment that arrives late is useless, and
        // two instances sharing a queue would each get half the lines.
        cfg.ReceiveEndpoint(opts.EffectiveQueueName, e =>
        {
            e.Durable = false;
            e.AutoDelete = true;
            e.DiscardFaultedMessages();
            e.DiscardSkippedMessages();
            e.ConfigureConsumeTopology = false;
            e.PrefetchCount = 32;

            KickMediaTopology.BindLiveTranscript(e, opts.NormalizedChannels); // no slugs → every channel
            e.ConfigureConsumer<LiveTranscriptConsumer>(ctx);
        });

        if (relayOpts.Enabled)
        {
            cfg.ReceiveEndpoint(opts.EffectiveQueueName + "-video", e =>
            {
                e.Durable = false;
                e.AutoDelete = true;
                e.DiscardFaultedMessages();
                e.DiscardSkippedMessages();
                e.ConfigureConsumeTopology = false;
                // Segments are multi-hundred-KB and must land in order per channel.
                e.ConcurrentMessageLimit = 1;
                e.PrefetchCount = 4;

                var relayChannels = relayOpts.NormalizedChannels.Length > 0 ? relayOpts.NormalizedChannels : opts.NormalizedChannels;
                KickMediaTopology.BindLiveVideo(e, relayChannels);
                e.ConfigureConsumer<LiveVideoSegmentConsumer>(ctx);
            });
        }
    });
});

var app = builder.Build();

// Assets live under /_/… so they can never collide with a channel slug (slugs have no '/').
var wwwroot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/_",
    FileProvider = new PhysicalFileProvider(wwwroot),
    ContentTypeProvider = new FileExtensionContentTypeProvider(),
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "public, max-age=300",
});

app.MapDefaultEndpoints();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/", () => Html(Path.Combine(wwwroot, "index.html")));

// Channels with a backlog or viewers right now + the configured allowlist — for the start page.
app.MapGet("/_/channels", (TranscriptFeed feed, SubtitlesOptions o) =>
    Results.Json(new { active = feed.ActiveSlugs, allowed = o.NormalizedChannels }));

app.MapGet("/{slug}", (string slug, SubtitlesOptions o) =>
    SubtitlesOptions.TryNormalizeSlug(slug, out var s) && o.Allows(s)
        ? Html(Path.Combine(wwwroot, "watch.html"))
        : Results.NotFound());

// Recent lines as JSON (what a fresh EventSource would get first) — handy for curl and tests.
app.MapGet("/{slug}/recent", (string slug, SubtitlesOptions o, TranscriptFeed feed, long? after) =>
    SubtitlesOptions.TryNormalizeSlug(slug, out var s) && o.Allows(s)
        ? Results.Json(feed.Backlog(s, after ?? 0), Sse.Json)
        : Results.NotFound());

app.MapGet("/{slug}/events", async (string slug, HttpContext ctx, SubtitlesOptions o, TranscriptFeed feed) =>
{
    if (!SubtitlesOptions.TryNormalizeSlug(slug, out var s) || !o.Allows(s))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await Sse.StreamAsync(ctx, feed, s, Sse.ResumeAfter(ctx.Request), TimeSpan.FromSeconds(o.KeepAliveSeconds));
});

app.MapRelay(wwwroot);

app.Logger.LogInformation("Subtitles site starting — channels: {Channels}; queue '{Queue}'; backlog {Backlog}s/{Items} items; relay {Relay}",
    opts.NormalizedChannels.Length == 0 ? "ALL" : string.Join(",", opts.NormalizedChannels), opts.EffectiveQueueName, opts.BacklogSeconds, opts.MaxBacklogItems,
    relayOpts.Enabled
        ? $"ON (buffer {relayOpts.BufferSeconds}s, delay {relayOpts.DefaultDelaySeconds}s default / {relayOpts.MaxDelaySeconds}s max, {relayOpts.MaxViewersPerChannel} viewer(s) per channel, channels: {(relayOpts.NormalizedChannels.Length > 0 ? string.Join(",", relayOpts.NormalizedChannels) : opts.NormalizedChannels.Length > 0 ? string.Join(",", opts.NormalizedChannels) : "ALL")})"
        : "off (no Subtitles:Relay:Token)");

app.Run();

static IResult Html(string path) =>
    Results.File(path, "text/html; charset=utf-8", enableRangeProcessing: false);
