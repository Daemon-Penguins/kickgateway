using MassTransit;
using TailoredApps.KickGateway.Contracts.Channels;
using TailoredApps.KickGateway.Contracts.Events;

namespace TailoredApps.KickGateway.Contracts;

/// <summary>
/// Shared topology contract between the gateway (publisher) and every
/// subscriber. Switches from MassTransit's default fanout exchanges to topic
/// exchanges with the broadcaster slug as the routing key, so subscribers can
/// bind their queue to only the channels they care about (broker-side filter,
/// no wasted CPU on the consumer).
/// </summary>
public static class KickEventTopology
{
    /// <summary>
    /// Wildcard binding key. Use this when a subscriber wants every channel
    /// (equivalent to the legacy fanout behavior).
    /// </summary>
    public const string AllChannels = "#";

    /// <summary>
    /// Apply on the gateway's <c>UsingRabbitMq</c> configurator. Sets the
    /// publish exchange type to <c>topic</c> for every Kick contract and
    /// makes the broadcaster slug the routing key. Idempotent — safe to call
    /// once per bus.
    /// </summary>
    public static void ConfigurePublishTopology(IRabbitMqBusFactoryConfigurator cfg)
    {
        ConfigureMessageType<ChatMessageSent>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<ChannelFollowed>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<ChannelSubscriptionNew>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<ChannelSubscriptionGifts>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<ChannelSubscriptionRenewal>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<LivestreamStatusUpdated>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<LivestreamMetadataUpdated>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<ModerationBanned>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<KicksGifted>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<ChannelRewardRedemptionUpdated>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<KickEventUnknown>(cfg, m => m.BroadcasterSlug);

        // On-demand channel statistics (request → response), routed by slug too.
        ConfigureMessageType<ChannelStatsRequested>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<ChannelStats>(cfg, m => m.BroadcasterSlug);

        // On-demand channel videos (VOD listing, request → response), routed by slug.
        ConfigureMessageType<ChannelVideosRequested>(cfg, m => m.BroadcasterSlug);
        ConfigureMessageType<ChannelVideos>(cfg, m => m.BroadcasterSlug);
    }

    /// <summary>
    /// Bind a receive endpoint to a Kick event exchange with channel-level
    /// filtering. Each entry in <paramref name="channelSlugs"/> creates a
    /// separate AMQP binding; pass <see cref="AllChannels"/> (or no slugs at
    /// all) to receive every channel.
    /// </summary>
    public static void BindKickEvent<TEvent>(
        IRabbitMqReceiveEndpointConfigurator endpoint,
        params string[] channelSlugs)
        where TEvent : class
    {
        if (channelSlugs.Length == 0)
        {
            endpoint.Bind<TEvent>(b =>
            {
                b.ExchangeType = "topic";
                b.RoutingKey = AllChannels;
            });
            return;
        }

        foreach (var slug in channelSlugs)
        {
            var key = string.IsNullOrWhiteSpace(slug) ? AllChannels : slug.ToLowerInvariant();
            endpoint.Bind<TEvent>(b =>
            {
                b.ExchangeType = "topic";
                b.RoutingKey = key;
            });
        }
    }

    private static void ConfigureMessageType<T>(
        IRabbitMqBusFactoryConfigurator cfg,
        Func<T, string> routingKeySelector)
        where T : class
    {
        cfg.Publish<T>(p => p.ExchangeType = "topic");
        cfg.Send<T>(s => s.UseRoutingKeyFormatter(ctx =>
            (routingKeySelector(ctx.Message) ?? string.Empty).ToLowerInvariant()));
    }
}
