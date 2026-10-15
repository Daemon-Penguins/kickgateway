using MassTransit;
using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Twitch;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.TwitchGateway.Capture;
using TailoredApps.TwitchGateway.Contracts.Media;

// Twitch audio capture: for every roster channel with CaptureAudio on, pull the live audio_only HLS
// rendition the way Twitch's own player does (GQL playback token + usher) and publish the raw segments as
// Contracts.Twitch.Media.TwitchAudioSegment (direct bus publish, short TTL) for the Twitch transcriber.
// Its own process and compose profile so the audio path can be deployed, stopped or moved to another
// box without touching the chat listener.

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<TwitchCaptureOptions>(builder.Configuration.GetSection(TwitchCaptureOptions.SectionName));
builder.Services.AddTwitchIntegration(builder.Configuration);

var connStr = builder.Configuration.GetConnectionString("KickGateway")
              ?? throw new InvalidOperationException("Missing connection string 'KickGateway'");
builder.Services.AddDbContext<KickGatewayDbContext>(o => o.UseSqlServer(connStr));

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });
        TwitchMediaTopology.ConfigurePublishTopology(cfg);
    });
});

builder.Services.AddSingleton<TwitchAudioCaptureLoop>();
builder.Services.AddHostedService<TwitchCaptureService>();

await builder.Build().RunAsync();
