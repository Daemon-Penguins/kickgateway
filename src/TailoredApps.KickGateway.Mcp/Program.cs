using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Mcp;

// MCP server (stdio) for Kick chat analytics. It is a thin adapter: every tool is one GET against
// the gateway's /api/analytics/* endpoints, authenticated with the analytics API key. Launched by
// the MCP client (Claude Code / Claude Desktop / …), so stdout belongs to the protocol — all logging
// goes to stderr.

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // The MCP client starts us from an arbitrary working directory; read appsettings next to the binary.
    ContentRootPath = AppContext.BaseDirectory,
});

// Keep the key out of MCP client config files if you like: `dotnet user-secrets set KickGateway:ApiKey …`.
// Environment variables still win.
builder.Configuration.AddUserSecrets<GatewayOptions>(optional: true);
builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.SectionName));
builder.Services.AddHttpClient<GatewayClient>((sp, http) =>
    {
        var o = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
        http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds));
    })
    // A missing/invalid key would otherwise be bounced to the admin login (→ kick.com HTML).
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "kick-chat-analytics", Version = "1.0.0" };
        o.ServerInstructions = Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<ChatAnalyticsTools>();

await builder.Build().RunAsync();

public partial class Program
{
    private const string Instructions = """
        Kick chat analytics for the channels this gateway ingests (webhooks + realtime stream). Use it to understand
        the dynamics between chatters and to build personal profiles of chatters.

        Typical flow:
        1. list_channels → pick a channel; channel_overview for the big picture.
        2. interaction_graph for who talks to whom, cliques (communities), hubs and bridges.
        3. chatter_profile for one person; chatter_relationship for a pair; find_chatters when you only have a partial name.
        4. search_messages / message_context to read the actual messages behind any number before drawing conclusions
           about tone, intent or relationships — the aggregates are signals, not verdicts.

        Conventions: all times are UTC. 'from'/'to' accept ISO-8601, look-backs like 12h/7d/4w, or 'all'. Channel-level tools
        default to the last 7 days; per-person tools default to all history (a response 'window' without 'from' = since the
        beginning). Chatters are identified by Kick user id (stable);
        usernames can change. Graph nodes keyed '@name' were mentioned but never seen chatting. If results are empty or look
        stale, check analytics_status (the read model trails ingestion by ~30 s and backfills history on first start).
        """;
}
