using MassTransit;

namespace TailoredApps.KickGateway.Contracts.Realtime.Media;

/// <summary>
/// Publish/consume topology for the live-video media stream (<see cref="LiveVideoSegment"/>).
/// Its own topic exchange, keyed by broadcaster slug — separate from both the webhook and the
/// real-time-event exchanges so a subscriber opts into the (heavy, best-effort) video firehose
/// deliberately.
/// </summary>
public static class KickMediaTopology
{
    /// <summary>Wildcard binding key — every channel.</summary>
    public const string AllChannels = "#";

    /// <summary>Apply on the publisher's <c>UsingRabbitMq</c> configurator. Idempotent.</summary>
    public static void ConfigurePublishTopology(IRabbitMqBusFactoryConfigurator cfg)
    {
        cfg.Publish<LiveVideoSegment>(p => p.ExchangeType = "topic");
        cfg.Send<LiveVideoSegment>(s => s.UseRoutingKeyFormatter(ctx =>
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
    {
        if (channelSlugs.Length == 0)
        {
            endpoint.Bind<LiveVideoSegment>(b =>
            {
                b.ExchangeType = "topic";
                b.RoutingKey = AllChannels;
            });
            return;
        }

        foreach (var slug in channelSlugs)
        {
            var key = string.IsNullOrWhiteSpace(slug) ? AllChannels : slug.ToLowerInvariant();
            endpoint.Bind<LiveVideoSegment>(b =>
            {
                b.ExchangeType = "topic";
                b.RoutingKey = key;
            });
        }
    }
}
