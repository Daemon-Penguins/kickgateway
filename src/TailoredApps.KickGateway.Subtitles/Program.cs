using MassTransit;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subtitles;

// Live subtitles site: GET /{slug} embeds Kick's own player (player.kick.com — the video never touches
// this server; Kick's CDN only allows browsers from kick.com origins, so a self-hosted player is not an
// option anyway) and overlays the channel's live transcripts, streamed to the page over Server-Sent
// Events from the LiveTranscript exchange. Nothing is stored here — the Api owns persistence.
//
// The subtitle trails the picture by roughly ChunkSeconds + inference − the player's own buffer, i.e.
// ~2–4 s with 5 s transcriber chunks. The page shows the measured lag. Query flags on /{slug}:
//   ?overlay=1   transparent captions only, no player — an OBS browser source over the real stream
//   ?muted=false start the embedded player unmuted (browsers may still require a click)

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var opts = builder.Configuration.GetSection(SubtitlesOptions.Section).Get<SubtitlesOptions>() ?? new SubtitlesOptions();
opts.Validate();
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<TranscriptFeed>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<LiveTranscriptConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        cfg.ReceiveEndpoint(opts.EffectiveQueueName, e =>
        {
            // Throwaway, per-instance queue: a subtitle that arrives late is useless, and two instances
            // sharing a queue would each show half the lines.
            e.Durable = false;
            e.AutoDelete = true;
            e.DiscardFaultedMessages();
            e.DiscardSkippedMessages();
            e.ConfigureConsumeTopology = false;
            e.PrefetchCount = 32;

            KickMediaTopology.BindLiveTranscript(e, opts.NormalizedChannels); // no slugs → every channel
            e.ConfigureConsumer<LiveTranscriptConsumer>(ctx);
        });
    });
});

var app = builder.Build();

// Assets live under /_/… so they can never collide with a channel slug (slugs have no '/').
var wwwroot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var contentTypes = new FileExtensionContentTypeProvider();
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/_",
    FileProvider = new PhysicalFileProvider(wwwroot),
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "public, max-age=300",
});

app.MapDefaultEndpoints();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/", () => Html(Path.Combine(wwwroot, "index.html")));

/// Channels with a backlog or viewers right now + the configured allowlist — for the start page.
app.MapGet("/_/channels", (TranscriptFeed feed, SubtitlesOptions o) =>
    Results.Json(new { active = feed.ActiveSlugs, allowed = o.NormalizedChannels }));

app.MapGet("/{slug}", (string slug, SubtitlesOptions o) =>
    SubtitlesOptions.TryNormalizeSlug(slug, out var s) && o.Allows(s)
        ? Html(Path.Combine(wwwroot, "watch.html"))
        : Results.NotFound());

/// Recent lines as JSON (what a fresh EventSource would get first) — handy for curl and tests.
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

app.Logger.LogInformation("Subtitles site starting — channels: {Channels}; queue '{Queue}'; backlog {Backlog}s/{Items} items",
    opts.NormalizedChannels.Length == 0 ? "ALL" : string.Join(",", opts.NormalizedChannels), opts.EffectiveQueueName, opts.BacklogSeconds, opts.MaxBacklogItems);

app.Run();

static IResult Html(string path) =>
    Results.File(path, "text/html; charset=utf-8", enableRangeProcessing: false);
