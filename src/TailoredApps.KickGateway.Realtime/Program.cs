using MassTransit;
using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Contracts.Realtime;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Realtime;
using TailoredApps.KickGateway.Realtime.Ingest;
using TailoredApps.KickGateway.Realtime.Mapping;
using TailoredApps.KickGateway.Realtime.Pusher;
using TailoredApps.KickGateway.Realtime.Video;

// Real-time listener. Reads the managed-broadcaster roster from SQL, resolves each channel's
// Pusher ids via the Cloudflare-bypass sidecar, subscribes to Kick's Pusher WebSocket, and
// republishes the full event catalogue onto the realtime topic exchanges (inbox+outbox). When
// enabled, it also forwards live HLS segments (best-effort, direct publish).

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Options
builder.Services.Configure<RealtimeOptions>(builder.Configuration.GetSection(RealtimeOptions.SectionName));

// Kick integration → IKickChannelClient + IKickSidecarFetcher (+ the sidecar named HttpClient).
builder.Services.AddKickIntegration(builder.Configuration);

// HLS CDN client for live-video capture (browser UA; kick.com CDN is plain-.NET reachable).
builder.Services.AddHttpClient(RealtimeHttpClients.Hls,
    c => c.DefaultRequestHeaders.UserAgent.ParseAdd(RealtimeHttpClients.BrowserUa));

// Shared DB (roster reads + realtime inbox). The Api owns migrations; this process only reads
// the roster and writes ReceivedRealtimeEvent rows.
var connStr = builder.Configuration.GetConnectionString("KickGateway")
              ?? throw new InvalidOperationException("Missing connection string 'KickGateway'");
builder.Services.AddDbContext<KickGatewayDbContext>(opts => opts.UseSqlServer(connStr));

// MassTransit: EF bus outbox for realtime events + the realtime/media publish topologies.
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<KickGatewayDbContext>(o =>
    {
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.UseSqlServer();
        o.UseBusOutbox();
    });

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        // Realtime events go through the outbox; live-video segments are published directly
        // via IBus (best-effort). Both topologies asserted here so the exchanges exist.
        KickRealtimeTopology.ConfigurePublishTopology(cfg);
        KickMediaTopology.ConfigurePublishTopology(cfg);

        // Pure publisher — no receive endpoints.
    });
});

builder.Services.AddSingleton<RosterProvider>();
builder.Services.AddSingleton<RealtimeFrameMapper>();
builder.Services.AddSingleton<RealtimeIngestService>();
builder.Services.AddSingleton<HlsCaptureLoop>();

builder.Services.AddHostedService<PusherRealtimeService>();
builder.Services.AddHostedService<LiveVideoCaptureService>();

builder.Build().Run();
