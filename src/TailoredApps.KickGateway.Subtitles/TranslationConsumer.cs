using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subtitles;

/// <summary>Bus → <see cref="TranscriptFeed.AttachTranslation"/>: a translation lands on its transcript and is pushed to viewers as a <c>translation</c> event.</summary>
public sealed class TranslationConsumer(TranscriptFeed feed, ILogger<TranslationConsumer> log) : IConsumer<LiveTranscriptTranslation>
{
    public Task Consume(ConsumeContext<LiveTranscriptTranslation> context)
    {
        var m = context.Message;
        var ev = feed.AttachTranslation(m);
        if (ev is null)
            log.LogDebug("[{Slug}] translation ({Src}→{Dst}) has no transcript to attach to — dropped", m.BroadcasterSlug, m.SourceLanguage, m.TargetLanguage);
        else
            log.LogDebug("[{Slug}] #{Id} {Src}→{Dst} via {Provider}: {Text}", ev.Slug, ev.Id, m.SourceLanguage, m.TargetLanguage, m.Provider,
                m.Text.Length > 80 ? m.Text[..77] + "..." : m.Text);
        return Task.CompletedTask;
    }
}
