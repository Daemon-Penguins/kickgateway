using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick.Channels;
using TailoredApps.Integrations.Kick.Clips;
using TailoredApps.Integrations.Kick.Sidecar;
using TailoredApps.Integrations.Kick.Videos;

namespace TailoredApps.Integrations.Kick;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IKickClient"/> + <see cref="IKickSignatureVerifier"/>
    /// plus their named HttpClients ("KickAuth" + "KickApi"). Per-client OAuth
    /// credentials are NOT bound from configuration — the gateway looks them
    /// up by id from its own store and passes them per call.
    /// </summary>
    public static IServiceCollection AddKickIntegration(this IServiceCollection services, IConfiguration configuration, string sectionName = KickGlobalDefaults.SectionName)
    {
        services.Configure<KickGlobalDefaults>(configuration.GetSection(sectionName));
        return services.AddKickIntegrationInternal();
    }

    public static IServiceCollection AddKickIntegration(this IServiceCollection services, Action<KickGlobalDefaults> configure)
    {
        services.Configure(configure);
        return services.AddKickIntegrationInternal();
    }

    private static IServiceCollection AddKickIntegrationInternal(this IServiceCollection services)
    {
        services.AddHttpClient(KickClient.AuthHttpClientName, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<KickGlobalDefaults>>().Value;
            http.BaseAddress = new Uri(opts.AuthBaseUrl);
        });
        services.AddHttpClient(KickClient.ApiHttpClientName, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<KickGlobalDefaults>>().Value;
            http.BaseAddress = new Uri(opts.ApiBaseUrl);
        });

        // The sidecar fetcher talks to the clips-fetcher at an absolute URL built from
        // config, so this named client needs no base address. Shared by the clips +
        // channel-stats clients.
        services.AddHttpClient(KickSidecarFetcher.HttpClientName);

        services.AddSingleton<IKickClient, KickClient>();
        services.AddSingleton<IKickSignatureVerifier, KickSignatureVerifier>();
        services.AddSingleton<IKickSidecarFetcher, KickSidecarFetcher>();
        services.AddSingleton<IKickClipsClient, KickClipsClient>();
        services.AddSingleton<IKickChannelClient, KickChannelClient>();
        services.AddSingleton<IKickVideosClient, KickVideosClient>();
        return services;
    }
}
