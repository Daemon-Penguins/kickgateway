using MassTransit;
using TailoredApps.TwitchGateway.Contracts.Channel;
using TailoredApps.TwitchGateway.Contracts.Chat;

namespace TailoredApps.TwitchGateway.Contracts;

/// <summary>
/// Publish/consume topology for the Twitch event family: one topic exchange per contract type,
/// routing key = the broadcaster's Twitch login (lowercase), so a subscriber binds <c>#</c>
/// (every channel) or a specific login. Same shape as the Kick helpers, separate exchanges.
/// </summary>
public static class TwitchEventTopology
{
    /// <summary>Wildcard binding key - every channel.</summary>
    public const string AllChannels = "#";

    /// <summary>Apply on the listener's (and any subscriber's) <c>UsingRabbitMq</c> configurator. Idempotent.</summary>
    public static void ConfigurePublishTopology(IRabbitMqBusFactoryConfigurator cfg)
    {
        Configure<TwitchChatMessage>(cfg);
        Configure<TwitchChatNotification>(cfg);
        Configure<TwitchChatMessageDeleted>(cfg);
        Configure<TwitchChatCleared>(cfg);
        Configure<TwitchChatUserMessagesCleared>(cfg);
        Configure<TwitchChatSettingsUpdated>(cfg);

        Configure<TwitchStreamOnline>(cfg);
        Configure<TwitchStreamOffline>(cfg);
        Configure<TwitchChannelUpdated>(cfg);

        Configure<TwitchEventUnknown>(cfg);

        // On-demand stream stats (request -> response), routed by login too - the Twitch counterpart of
        // the Kick ChannelStatsRequested / ChannelStats pair.
        ConfigureRouted<TwitchStreamStatsRequested>(cfg, m => m.BroadcasterLogin);
        ConfigureRouted<TwitchStreamStats>(cfg, m => m.BroadcasterLogin);
    }

    /// <summary>
    /// Bind a receive endpoint to one Twitch event exchange with channel-level filtering. Pass no
    /// logins (or <see cref="AllChannels"/>) for the firehose.
    /// </summary>
    public static void BindTwitchEvent<TEvent>(IRabbitMqReceiveEndpointConfigurator endpoint, params string[] broadcasterLogins)
        where TEvent : class, ITwitchEvent
        => BindRouted<TEvent>(endpoint, broadcasterLogins);

    /// <summary>
    /// Bind a receive endpoint to the <see cref="TwitchStreamStatsRequested"/> exchange - what the gateway's
    /// Api does to answer stats requests. Subscribers normally use <c>IRequestClient&lt;TwitchStreamStatsRequested&gt;</c>
    /// instead; binding the request type yourself is for a second responder.
    /// </summary>
    public static void BindTwitchStreamStatsRequests(IRabbitMqReceiveEndpointConfigurator endpoint, params string[] broadcasterLogins)
        => BindRouted<TwitchStreamStatsRequested>(endpoint, broadcasterLogins);

    private static void BindRouted<TMessage>(IRabbitMqReceiveEndpointConfigurator endpoint, string[] broadcasterLogins)
        where TMessage : class
    {
        if (broadcasterLogins.Length == 0)
        {
            endpoint.Bind<TMessage>(b =>
            {
                b.ExchangeType = "topic";
                b.RoutingKey = AllChannels;
            });
            return;
        }

        foreach (var login in broadcasterLogins)
        {
            var key = string.IsNullOrWhiteSpace(login) ? AllChannels : login.Trim().ToLowerInvariant();
            endpoint.Bind<TMessage>(b =>
            {
                b.ExchangeType = "topic";
                b.RoutingKey = key;
            });
        }
    }

    private static void Configure<T>(IRabbitMqBusFactoryConfigurator cfg)
        where T : class, ITwitchEvent
        => ConfigureRouted<T>(cfg, m => m.BroadcasterLogin);

    private static void ConfigureRouted<T>(IRabbitMqBusFactoryConfigurator cfg, Func<T, string?> routingKey)
        where T : class
    {
        cfg.Publish<T>(p => p.ExchangeType = "topic");
        cfg.Send<T>(s => s.UseRoutingKeyFormatter(ctx =>
            (routingKey(ctx.Message) ?? string.Empty).ToLowerInvariant()));
    }
}
