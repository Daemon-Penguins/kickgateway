using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.Transcriber;
using TailoredApps.KickGateway.Subscribers.Transcriber.Consumers;
using TailoredApps.KickGateway.Subscribers.Transcriber.Sinks;
using TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;

// Live-stream transcriber: consumes the LiveVideoSegment firehose, pipes each channel's segments
// through a long-lived ffmpeg (audio only → 16 kHz mono PCM), cuts the audio on pauses into
// ~15 s chunks and runs them through Whisper (whisper.cpp via Whisper.net, GPU when available).
// Every chunk's text is published as Contracts.Realtime.Media.LiveTranscript on its own
// slug-routed topic exchange and appended to per-channel daily .txt/.jsonl files.

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var opts = builder.Configuration.GetSection(TranscriberOptions.Section).Get<TranscriberOptions>() ?? new TranscriberOptions();
opts.Validate();
builder.Services.AddSingleton(opts);

builder.Services.AddSingleton<TranscriptionCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptionCoordinator>());
builder.Services.AddSingleton<WhisperModelProvider>();
builder.Services.AddSingleton(new TranscriptFilter(opts.MinConfidence, opts.SuppressPhrases,
    promptWords: opts.Uncensored ? WhisperWorker.ProfanityVocabulary : []));
builder.Services.AddHostedService<WhisperWorker>();

// Sinks: files (default temp/kickgateway-transcripts) and/or the bus.
string? outputDir = null;
if (opts.WriteFiles)
{
    outputDir = string.IsNullOrWhiteSpace(opts.OutputDir)
        ? Path.Combine(Path.GetTempPath(), "kickgateway-transcripts")
        : opts.OutputDir;
    outputDir = Path.GetFullPath(outputDir);
    Directory.CreateDirectory(outputDir);
    builder.Services.AddSingleton<ITranscriptSink>(new TranscriptFileSink(outputDir));
}
if (opts.PublishToBus)
    builder.Services.AddSingleton<ITranscriptSink, BusTranscriptSink>();

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

        // Declares both media exchanges (video in, transcripts out) with slug routing.
        KickMediaTopology.ConfigurePublishTopology(cfg);

        cfg.ReceiveEndpoint(opts.QueueName, e =>
        {
            // Throwaway queue: segments are large + ephemeral (short TTL) — don't pile them up while
            // the transcriber is offline, and never park multi-MB segments in an _error queue.
            e.Durable = false;
            e.AutoDelete = true;
            e.DiscardFaultedMessages();
            e.DiscardSkippedMessages();

            // Decoding needs publish order per channel → one segment at a time (feeding ffmpeg is cheap).
            e.ConcurrentMessageLimit = 1;
            e.PrefetchCount = 16;

            KickMediaTopology.BindLiveVideo(e, opts.Channels); // no slugs → "#" (all channels)
            e.ConfigureConsumer<LiveVideoSegmentConsumer>(ctx);
        });
    });
});

var host = builder.Build();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Transcriber");

// Fail fast on the two things that make the service pointless: no ffmpeg, or an unknown model name.
try
{
    log.LogInformation("ffmpeg: {Version}", AudioDecoder.Probe(opts.FfmpegPath));
}
catch (Exception ex)
{
    log.LogCritical(ex, "ffmpeg is not runnable at '{Path}'. Install ffmpeg (https://ffmpeg.org) or set Transcriber:FfmpegPath.", opts.FfmpegPath);
    return 1;
}

WhisperModelProvider models;
try
{
    models = host.Services.GetRequiredService<WhisperModelProvider>();
}
catch (Exception ex)
{
    log.LogCritical(ex, "Invalid Whisper model configuration");
    return 1;
}

log.LogInformation("Transcriber starting — channels: {Channels}; queue '{Queue}'; model {Model} ({Dir}); GPU {Gpu}; language {Lang}; files → {Files}; bus publish {Bus}",
    opts.Channels.Length == 0 ? "ALL" : string.Join(",", opts.Channels),
    opts.QueueName, models.ModelName, models.ModelDirectory, opts.UseGpu, opts.DescribeLanguageMode(),
    outputDir ?? "(off)", opts.PublishToBus);

await host.RunAsync();
return 0;
