using MassTransit;

namespace TailoredApps.TwitchGateway.Contracts.Media;

/// <summary>
/// Publish/consume topology for the Twitch live-media family: the raw audio stream
/// (<see cref="TwitchAudioSegment"/>) and the speech-to-text derived from it
/// (<see cref="TwitchLiveTranscript"/>). Own topic exchanges, keyed by the broadcaster login.
/// </summary>
public static class TwitchMediaTopology
{
    public const string AllChannels = "#";

    /// <summary>Apply on the publisher's (and any subscriber's) <c>UsingRabbitMq</c> configurator. Idempotent.</summary>
    public static void ConfigurePublishTopology(IRabbitMqBusFactoryConfigurator cfg)
    {
        cfg.Publish<TwitchAudioSegment>(p => p.ExchangeType = "topic");
        cfg.Send<TwitchAudioSegment>(s => s.UseRoutingKeyFormatter(ctx =>
            (ctx.Message.BroadcasterLogin ?? string.Empty).ToLowerInvariant()));

        cfg.Publish<TwitchLiveTranscript>(p => p.ExchangeType = "topic");
        cfg.Send<TwitchLiveTranscript>(s => s.UseRoutingKeyFormatter(ctx =>
            (ctx.Message.BroadcasterLogin ?? string.Empty).ToLowerInvariant()));
    }

    /// <summary>Bind a receive endpoint to the audio exchange (no logins = every channel). Use a short-lived, auto-delete queue: segments expire.</summary>
    public static void BindAudio(IRabbitMqReceiveEndpointConfigurator endpoint, params string[] broadcasterLogins)
        => Bind<TwitchAudioSegment>(endpoint, broadcasterLogins);

    /// <summary>Bind a receive endpoint to the transcript exchange (no logins = every channel).</summary>
    public static void BindTranscript(IRabbitMqReceiveEndpointConfigurator endpoint, params string[] broadcasterLogins)
        => Bind<TwitchLiveTranscript>(endpoint, broadcasterLogins);

    private static void Bind<TMessage>(IRabbitMqReceiveEndpointConfigurator endpoint, string[] broadcasterLogins)
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
}
