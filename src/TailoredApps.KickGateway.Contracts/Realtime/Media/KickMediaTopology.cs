using MassTransit;

namespace TailoredApps.KickGateway.Contracts.Realtime.Media;

/// <summary>
/// Publish/consume topology for the live-media contract family: the raw video firehose
/// (<see cref="LiveVideoSegment"/>) and the speech-to-text derived from it
/// (<see cref="LiveTranscript"/>). Each has its own topic exchange, keyed by broadcaster slug —
/// separate from both the webhook and the real-time-event exchanges, so a subscriber opts into
/// the (heavy, best-effort) video stream or the (light) transcript stream deliberately.
/// </summary>
public static class KickMediaTopology
{
    /// <summary>Wildcard binding key — every channel.</summary>
    public const string AllChannels = "#";

    /// <summary>Apply on the publisher's (and any subscriber's) <c>UsingRabbitMq</c> configurator. Idempotent.</summary>
    public static void ConfigurePublishTopology(IRabbitMqBusFactoryConfigurator cfg)
    {
        cfg.Publish<LiveVideoSegment>(p => p.ExchangeType = "topic");
        cfg.Send<LiveVideoSegment>(s => s.UseRoutingKeyFormatter(ctx =>
            (ctx.Message.BroadcasterSlug ?? string.Empty).ToLowerInvariant()));

        cfg.Publish<LiveTranscript>(p => p.ExchangeType = "topic");
        cfg.Send<LiveTranscript>(s => s.UseRoutingKeyFormatter(ctx =>
            (ctx.Message.BroadcasterSlug ?? string.Empty).ToLowerInvariant()));
    }

    /// <summary>
    /// Bind a receive endpoint to the live-video exchange. Pass no slugs (or
    /// <see cref="AllChannels"/>) for every channel, or specific slugs to filter broker-side.
    /// Subscribers typically want a short-lived / auto-expiring queue since segments are ephemeral.
    /// </summary>
    public static void BindLiveVideo(
        IRabbitMqReceiveEndpointConfigurator endpoint,
        params string[] channelSlugs)
        => Bind<LiveVideoSegment>(endpoint, channelSlugs);

    /// <summary>
    /// Bind a receive endpoint to the live-transcript exchange. Pass no slugs (or
    /// <see cref="AllChannels"/>) for every channel, or specific slugs to filter broker-side.
    /// Transcripts are small and carry no TTL, so a normal durable queue is fine.
    /// </summary>
    public static void BindLiveTranscript(
        IRabbitMqReceiveEndpointConfigurator endpoint,
        params string[] channelSlugs)
        => Bind<LiveTranscript>(endpoint, channelSlugs);

    private static void Bind<TMessage>(IRabbitMqReceiveEndpointConfigurator endpoint, string[] channelSlugs)
        where TMessage : class
    {
        if (channelSlugs.Length == 0)
        {
            endpoint.Bind<TMessage>(b =>
            {
                b.ExchangeType = "topic";
                b.RoutingKey = AllChannels;
            });
            return;
        }

        foreach (var slug in channelSlugs)
        {
            var key = string.IsNullOrWhiteSpace(slug) ? AllChannels : slug.ToLowerInvariant();
            endpoint.Bind<TMessage>(b =>
            {
                b.ExchangeType = "topic";
                b.RoutingKey = key;
            });
        }
    }
}
