using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Twitch;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.TwitchGateway.Contracts;
using TailoredApps.TwitchGateway.Listener;
using TailoredApps.TwitchGateway.Listener.EventSub;
using TailoredApps.TwitchGateway.Listener.Ingest;
using TailoredApps.TwitchGateway.Listener.Roster;
using TailoredApps.TwitchGateway.Listener.Streams;

// Twitch listener: EventSub WebSocket chat for every channel in the TwitchChannels roster (using the
// gateway's bot account token) + Helix stream-state polling, recorded in the ReceivedTwitchEvents inbox
// and published through the EF outbox as Contracts.Twitch.* on login-routed topic exchanges. The Kick
// realtime listener is a separate process on purpose: either side can be stopped without the other.

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<TwitchListenerOptions>(builder.Configuration.GetSection(TwitchListenerOptions.SectionName));
builder.Services.AddTwitchIntegration(builder.Configuration);

// Shared gateway database: the roster + bot token are managed by the Api; this process reads them and
// writes the Twitch inbox. Migrations are applied by the Api.
var connStr = builder.Configuration.GetConnectionString("KickGateway")
              ?? throw new InvalidOperationException("Missing connection string 'KickGateway'");
builder.Services.AddDbContext<KickGatewayDbContext>(o => o.UseSqlServer(connStr));

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

        TwitchEventTopology.ConfigurePublishTopology(cfg);
        // No receive endpoints: this process only publishes.
    });
});

builder.Services.AddSingleton<TwitchAccessTokenSource>();
builder.Services.AddSingleton<TwitchRosterProvider>();
builder.Services.AddSingleton<TwitchIngestService>();
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<TwitchListenerOptions>>().Value;
    var ingest = sp.GetRequiredService<TwitchIngestService>();
    return new TwitchIngestQueue(o.IngestWorkers, o.IngestQueueCapacity, o.IngestRetries,
        TimeSpan.FromMilliseconds(Math.Max(0, o.IngestRetryDelayMs)), ingest.IngestAsync,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Twitch.Ingest"));
});
builder.Services.AddHostedService<EventSubSessionManager>();
builder.Services.AddHostedService<StreamStatePoller>();

var host = builder.Build();
await host.RunAsync();
