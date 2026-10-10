using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.Translator;
using TailoredApps.KickGateway.Subscribers.Translator.Consumers;
using TailoredApps.KickGateway.Subscribers.Translator.Translation;

// Live-transcript translator: consumes LiveTranscript, and for slices in one of the configured source
// languages (default: German) asks the provider — DeepL by default (free plan: 500k characters/month), or a
// local/remote LLM over an OpenAI-compatible endpoint (Ollama, LiteLLM, …), or Anthropic — for a
// translation into the target language (default: Polish), segment by segment so timings survive, then
// publishes Contracts.Realtime.Media.LiveTranscriptTranslation on its own slug-routed exchange. The Api
// stores it next to the transcript; the subtitles site shows it.

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var opts = builder.Configuration.GetSection(TranslatorOptions.Section).Get<TranslatorOptions>() ?? new TranslatorOptions();
opts.Validate();
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(opts.Provider);

builder.Services.AddHttpClient("provider", c => c.Timeout = TimeSpan.FromSeconds(opts.Provider.TimeoutSeconds + 5));
builder.Services.AddSingleton<ITranslator>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("provider");
    return opts.Provider.NormalizedName switch
    {
        "deepl" => new DeepLTranslator(http, opts.Provider),
        "openai" => new OpenAiCompatibleTranslator(http, opts.Provider),
        "anthropic" => new AnthropicTranslator(http, opts.Provider),
        _ => new StubTranslator(opts.Provider),
    };
});

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

        // Declares the media exchanges (incl. the translation one we publish to) with slug routing.
        KickMediaTopology.ConfigurePublishTopology(cfg);

        cfg.ReceiveEndpoint(opts.EffectiveQueueName, e =>
        {
            // Throwaway queue: a translation that arrives minutes late is useless for captions, and a backlog
            // after downtime must not turn into provider calls (the policy also drops stale slices).
            e.Durable = false;
            e.AutoDelete = true;
            e.DiscardFaultedMessages();
            e.DiscardSkippedMessages();
            e.ConfigureConsumeTopology = false;
            e.ConcurrentMessageLimit = opts.MaxConcurrency;
            e.PrefetchCount = opts.MaxConcurrency * 2;

            KickMediaTopology.BindLiveTranscript(e, opts.NormalizedChannels); // no slugs → every channel
            e.ConfigureConsumer<LiveTranscriptConsumer>(ctx);
        });
    });
});

var host = builder.Build();
var translator = host.Services.GetRequiredService<ITranslator>();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Translator").LogInformation(
    "Translator starting — {Src} → {Dst}; channels: {Channels}; provider {Provider} at {Url}; min confidence {Conf}; max age {Age}s; queue '{Queue}'",
    string.Join(",", opts.NormalizedSourceLanguages), opts.NormalizedTargetLanguage,
    opts.NormalizedChannels.Length == 0 ? "ALL" : string.Join(",", opts.NormalizedChannels),
    translator.Name, opts.Provider.NormalizedName == "stub" ? "(no network)" : opts.Provider.EffectiveBaseUrl,
    opts.MinConfidence, opts.MaxAgeSeconds, opts.EffectiveQueueName);

await host.RunAsync();
