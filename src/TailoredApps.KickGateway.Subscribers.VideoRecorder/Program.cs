using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.VideoRecorder;
using TailoredApps.KickGateway.Subscribers.VideoRecorder.Consumers;

// Sample/test subscriber: consumes the live-video firehose (LiveVideoSegment) and reassembles it
// into playable files on disk — the end-to-end proof that segments can be grabbed from the queue
// AND rebuilt into valid media. Just by binding the media exchange it also declares
// `...Realtime.Media:LiveVideoSegment` in RabbitMQ at startup (visible even before the first segment).

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Output directory: Recorder:OutputDir, else a stable temp folder. Absolute path is logged below.
var outputDir = builder.Configuration["Recorder:OutputDir"];
if (string.IsNullOrWhiteSpace(outputDir))
    outputDir = Path.Combine(Path.GetTempPath(), "kickgateway-video");
outputDir = Path.GetFullPath(outputDir);
Directory.CreateDirectory(outputDir);

builder.Services.AddSingleton(new RecorderOptions(outputDir));
builder.Services.AddSingleton<VideoFileAssembler>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<LiveVideoSegmentConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        KickMediaTopology.ConfigurePublishTopology(cfg);

        cfg.ReceiveEndpoint("video-recorder", e =>
        {
            // Throwaway queue: segments are large + ephemeral (short TTL), so don't keep piling
            // them up while the recorder is offline.
            e.Durable = false;
            e.AutoDelete = true;

            // Reassembly needs publish order → process one segment at a time.
            e.ConcurrentMessageLimit = 1;
            e.PrefetchCount = 16;

            KickMediaTopology.BindLiveVideo(e); // all channels (routing key "#")
            e.ConfigureConsumer<LiveVideoSegmentConsumer>(ctx);
        });
    });
});

var host = builder.Build();

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("VideoRecorder")
    .LogInformation("Video recorder online — writing reassembled files to {Dir}", outputDir);

host.Run();
