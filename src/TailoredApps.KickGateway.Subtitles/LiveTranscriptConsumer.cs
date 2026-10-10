using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subtitles;

/// <summary>Bus → <see cref="TranscriptFeed"/>. Nothing is stored: the Api owns persistence, this site only shows what is live.</summary>
public sealed class LiveTranscriptConsumer(TranscriptFeed feed, ILogger<LiveTranscriptConsumer> log) : IConsumer<LiveTranscript>
{
    public Task Consume(ConsumeContext<LiveTranscript> context)
    {
        var ev = feed.Publish(context.Message);
        if (ev is null)
            log.LogDebug("Ignoring LiveTranscript without a usable slug/text");
        else
            log.LogDebug("[{Slug}] #{Id} [{Lang}] {Lag:F1}s behind → {Viewers} viewer(s): {Text}",
                ev.Slug, ev.Id, ev.Language, ev.LagSeconds, feed.SubscriberCount(ev.Slug), ev.Text.Length > 80 ? ev.Text[..77] + "..." : ev.Text);
        return Task.CompletedTask;
    }
}
