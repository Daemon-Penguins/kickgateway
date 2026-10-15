using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Twitch.Auth;
using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.Integrations.Twitch.Playback;

namespace TailoredApps.Integrations.Twitch;

public static class ServiceCollectionExtensions
{
    public const string AuthHttpClientName = "TwitchAuth";
    public const string HelixHttpClientName = "TwitchHelix";
    public const string GqlHttpClientName = "TwitchGql";
    public const string PlaybackHttpClientName = "TwitchPlayback";

    /// <summary>
    /// Registers <see cref="ITwitchAuthClient"/>, <see cref="ITwitchHelixClient"/> and
    /// <see cref="ITwitchPlaybackClient"/> with their named HttpClients, bound from the <c>Twitch</c> section.
    /// </summary>
    public static IServiceCollection AddTwitchIntegration(this IServiceCollection services, IConfiguration configuration, string sectionName = TwitchOptions.SectionName)
    {
        services.Configure<TwitchOptions>(configuration.GetSection(sectionName));
        return services.AddTwitchIntegrationInternal();
    }

    public static IServiceCollection AddTwitchIntegration(this IServiceCollection services, Action<TwitchOptions> configure)
    {
        services.Configure(configure);
        return services.AddTwitchIntegrationInternal();
    }

    private static IServiceCollection AddTwitchIntegrationInternal(this IServiceCollection services)
    {
        services.AddHttpClient(AuthHttpClientName, (sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<TwitchOptions>>().Value;
            http.BaseAddress = new Uri(o.AuthBaseUrl.TrimEnd('/') + "/");
        });
        services.AddHttpClient(HelixHttpClientName, (sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<TwitchOptions>>().Value;
            http.BaseAddress = new Uri(o.HelixBaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Client-Id", o.ClientId);
        });
        services.AddHttpClient(GqlHttpClientName, (sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<TwitchOptions>>().Value;
            http.DefaultRequestHeaders.TryAddWithoutValidation("Client-ID", o.GqlClientId);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(o.PlaybackUserAgent);
        });
        services.AddHttpClient(PlaybackHttpClientName, (sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<TwitchOptions>>().Value;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(o.PlaybackUserAgent);
            http.DefaultRequestHeaders.Referrer = new Uri("https://player.twitch.tv/");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://player.twitch.tv");
        });

        services.AddSingleton<ITwitchAuthClient, TwitchAuthClient>();
        services.AddSingleton<ITwitchHelixClient, TwitchHelixClient>();
        services.AddSingleton<ITwitchPlaybackClient, TwitchPlaybackClient>();
        return services;
    }
}
