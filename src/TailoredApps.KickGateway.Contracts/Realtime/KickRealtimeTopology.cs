using MassTransit;

namespace TailoredApps.KickGateway.Contracts.Realtime;

/// <summary>
/// Publish/consume topology for the <b>real-time</b> (Kick Pusher) contract family — the
/// sibling of <see cref="TailoredApps.KickGateway.Contracts.KickEventTopology"/> for the
/// signed-webhook family. Same shape: topic exchanges keyed by the broadcaster slug, so a
/// subscriber can bind <c>#</c> (firehose) or a specific slug. These are separate exchanges
/// from the webhook contracts, so the two streams never collide.
/// </summary>
public static class KickRealtimeTopology
{
    /// <summary>Wildcard binding key — every channel.</summary>
    public const string AllChannels = "#";

    /// <summary>
    /// Apply on the listener's (and any subscriber's) <c>UsingRabbitMq</c> configurator so
    /// every real-time contract publishes to a topic exchange routed by broadcaster slug.
    /// Idempotent.
    /// </summary>
    public static void ConfigurePublishTopology(IRabbitMqBusFactoryConfigurator cfg)
    {
        // chatrooms.{id}.v2
        Configure<RealtimeChatMessage>(cfg);
        Configure<RealtimeMessageDeleted>(cfg);
        Configure<RealtimeUserBanned>(cfg);
        Configure<RealtimeUserUnbanned>(cfg);
        Configure<RealtimePinnedMessageCreated>(cfg);
        Configure<RealtimePinnedMessageDeleted>(cfg);
        Configure<RealtimePollUpdate>(cfg);
        Configure<RealtimePollDelete>(cfg);
        Configure<RealtimeChatroomUpdated>(cfg);
        Configure<RealtimeChatroomClear>(cfg);

        // channel.{id}
        Configure<RealtimeSubscription>(cfg);
        Configure<RealtimeGiftedSubscriptions>(cfg);
        Configure<RealtimeLuckyGiftRecipients>(cfg);
        Configure<RealtimeStreamerLive>(cfg);
        Configure<RealtimeStreamEnd>(cfg);
        Configure<RealtimeStreamHost>(cfg);
        Configure<RealtimeFollowersUpdated>(cfg);
        Configure<RealtimeKicksGifted>(cfg);
        Configure<RealtimeRewardRedeemed>(cfg);

        Configure<KickRealtimeUnknown>(cfg);
    }

    /// <summary>
    /// Bind a receive endpoint to a real-time event exchange with channel-level filtering.
    /// Pass no slugs (or <see cref="AllChannels"/>) for the firehose.
    /// </summary>
    public static void BindKickRealtimeEvent<TEvent>(
        IRabbitMqReceiveEndpointConfigurator endpoint,
        params string[] channelSlugs)
        where TEvent : class, IKickRealtimeEvent
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

    private static void Configure<T>(IRabbitMqBusFactoryConfigurator cfg)
        where T : class, IKickRealtimeEvent
    {
        cfg.Publish<T>(p => p.ExchangeType = "topic");
        cfg.Send<T>(s => s.UseRoutingKeyFormatter(ctx =>
            (ctx.Message.BroadcasterSlug ?? string.Empty).ToLowerInvariant()));
    }
}
