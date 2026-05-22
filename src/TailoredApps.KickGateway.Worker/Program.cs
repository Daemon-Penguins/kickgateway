using MassTransit;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Events;
using TailoredApps.KickGateway.Worker.Consumers;

// Sample subscriber. Receives every event for every channel (legacy firehose).
// New subscribers wanting per-channel filtering bind with explicit slugs
// instead — see Subscribers.* sample apps.

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ChatMessageSentConsumer>();
    x.AddConsumer<ChannelFollowedConsumer>();
    x.AddConsumer<ChannelSubscriptionNewConsumer>();
    x.AddConsumer<ChannelSubscriptionGiftsConsumer>();
    x.AddConsumer<ChannelSubscriptionRenewalConsumer>();
    x.AddConsumer<LivestreamStatusUpdatedConsumer>();
    x.AddConsumer<LivestreamMetadataUpdatedConsumer>();
    x.AddConsumer<ModerationBannedConsumer>();
    x.AddConsumer<KicksGiftedConsumer>();
    x.AddConsumer<ChannelRewardRedemptionUpdatedConsumer>();
    x.AddConsumer<KickEventUnknownConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        // Publisher topology must match the gateway — even though we don't
        // publish ourselves, MassTransit asserts exchange types at bus start.
        KickEventTopology.ConfigurePublishTopology(cfg);

        var fmt = new KebabCaseEndpointNameFormatter(prefix: "worker", includeNamespace: false);

        cfg.ReceiveEndpoint(fmt.Consumer<ChatMessageSentConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ChatMessageSent>(e);
            e.ConfigureConsumer<ChatMessageSentConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<ChannelFollowedConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ChannelFollowed>(e);
            e.ConfigureConsumer<ChannelFollowedConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<ChannelSubscriptionNewConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ChannelSubscriptionNew>(e);
            e.ConfigureConsumer<ChannelSubscriptionNewConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<ChannelSubscriptionGiftsConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ChannelSubscriptionGifts>(e);
            e.ConfigureConsumer<ChannelSubscriptionGiftsConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<ChannelSubscriptionRenewalConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ChannelSubscriptionRenewal>(e);
            e.ConfigureConsumer<ChannelSubscriptionRenewalConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<LivestreamStatusUpdatedConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<LivestreamStatusUpdated>(e);
            e.ConfigureConsumer<LivestreamStatusUpdatedConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<LivestreamMetadataUpdatedConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<LivestreamMetadataUpdated>(e);
            e.ConfigureConsumer<LivestreamMetadataUpdatedConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<ModerationBannedConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ModerationBanned>(e);
            e.ConfigureConsumer<ModerationBannedConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<KicksGiftedConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<KicksGifted>(e);
            e.ConfigureConsumer<KicksGiftedConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<ChannelRewardRedemptionUpdatedConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ChannelRewardRedemptionUpdated>(e);
            e.ConfigureConsumer<ChannelRewardRedemptionUpdatedConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<KickEventUnknownConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<KickEventUnknown>(e);
            e.ConfigureConsumer<KickEventUnknownConsumer>(ctx);
        });
    });
});

var host = builder.Build();
host.Run();
