using Microsoft.Extensions.Options;

namespace TailoredApps.KickGateway.Mcp;

/// <summary>What both transports share: the gateway HTTP client and the MCP server with its tools + instructions.</summary>
internal static class McpComposition
{
    public const string Version = "1.4.0";

    public static void AddGateway(IServiceCollection services, IConfiguration config)
    {
        services.Configure<GatewayOptions>(config.GetSection(GatewayOptions.SectionName));
        services.AddHttpClient<GatewayClient>((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
                http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds));
            })
            // A missing/invalid key would otherwise be bounced to the admin login (→ kick.com HTML).
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }

    public static IMcpServerBuilder AddKickMcp(IServiceCollection services) =>
        services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "kick-chat-analytics", Version = Version };
                o.ServerInstructions = Instructions;
            })
            .WithTools<ChatAnalyticsTools>();

    private const string Instructions = """
        Kick chat analytics for the channels this gateway ingests (webhooks + realtime stream). Use it to understand
        the dynamics between chatters and to build personal profiles of chatters.

        Typical flow:
        1. list_channels → pick a channel; channel_overview for the big picture.
        2. interaction_graph for who talks to whom, cliques (communities), hubs and bridges.
        3. chatter_profile for one person; chatter_relationship for a pair; find_chatters when you only have a partial name.
        4. search_messages / message_context to read the actual messages behind any number before drawing conclusions
           about tone, intent or relationships — the aggregates are signals, not verdicts.
        5. When the channel has live transcripts (speech-to-text of the stream's audio; analytics_status / channel_overview
           say whether it does): stream_timeline for what the stream SAID, what HAPPENED and what chat WROTE in one
           chronology; search_transcripts to find when something was said; transcript_at for the speech behind one chat
           message; speech_reactions for the chat that followed one sentence, with its rate against the minute before.

        Conventions: all times are UTC. 'from'/'to' accept ISO-8601, look-backs like 12h/7d/4w, or 'all'. Channel-level tools
        default to the last 7 days (stream_timeline: the last hour); per-person tools default to all history (a response
        'window' without 'from' = since the beginning). Chatters are identified by Kick user id (stable);
        usernames can change. Graph nodes keyed '@name' were mentioned but never seen chatting. If results are empty or look
        stale, check analytics_status (the read model trails ingestion by ~30 s and backfills history on first start).
        Speech conventions: times are stream time; viewers watch with a player delay, so chat reactions to a sentence land
        ~10-20 s after its endedAt. Speech is best-effort — a gap means nothing was transcribed, not that nobody spoke — and
        'confidence' below ~0.5 is often noise (music, hallucinated fillers). Speech is the stream's audio, not only the
        streamer: guests, game audio and music are transcribed too.
        """;
}
