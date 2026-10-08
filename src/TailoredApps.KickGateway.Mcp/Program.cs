using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Mcp;

// MCP server for Kick chat analytics — a thin adapter: every tool is one GET against the gateway's
// /api/analytics/* endpoints, authenticated with the analytics API key. Two transports:
//  • stdio (default): launched by a local MCP client (Claude Code / Desktop). stdout belongs to the protocol,
//    so all logging goes to stderr.
//  • HTTP (`--http` or Mcp__Transport=http): Streamable HTTP at /mcp — the `mcp` container behind Traefik.
//    Clients authenticate with Mcp:ClientApiKey (defaults to KickGateway:ApiKey), as Bearer or X-Api-Key.

var useHttp = args.Contains("--http")
              || string.Equals(Environment.GetEnvironmentVariable("Mcp__Transport"), "http", StringComparison.OrdinalIgnoreCase);
if (useHttp) await RunHttpAsync(args);
else await RunStdioAsync(args);

public partial class Program
{
    private const int MinClientKeyLength = 24;

    private static async Task RunStdioAsync(string[] args)
    {
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

        AddGateway(builder.Services, builder.Configuration);
        AddKickMcp(builder.Services).WithStdioServerTransport();
        await builder.Build().RunAsync();
    }

    private static async Task RunHttpAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        AddGateway(builder.Services, builder.Configuration);
        AddKickMcp(builder.Services).WithHttpTransport(o => o.Stateless = true);

        var app = builder.Build();
        var clientKey = (app.Configuration["Mcp:ClientApiKey"] is { Length: > 0 } k ? k : app.Configuration["KickGateway:ApiKey"] ?? "").Trim();
        if (clientKey.Length < MinClientKeyLength)
            app.Logger.LogWarning("No client API key of at least {Min} chars configured — every /mcp request will be rejected", MinClientKeyLength);

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        // Fail closed: /mcp needs the key, and a missing/short configured key rejects everyone.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp") && !IsAuthorized(ctx.Request, clientKey))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                await ctx.Response.WriteAsJsonAsync(new { error = "missing or invalid API key" });
                return;
            }
            await next();
        });

        app.MapMcp("/mcp");
        await app.RunAsync();
    }

    private static void AddGateway(IServiceCollection services, IConfiguration config)
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

    private static IMcpServerBuilder AddKickMcp(IServiceCollection services) =>
        services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "kick-chat-analytics", Version = "1.1.0" };
                o.ServerInstructions = Instructions;
            })
            .WithTools<ChatAnalyticsTools>();

    private static bool IsAuthorized(HttpRequest request, string configured)
    {
        if (configured.Length < MinClientKeyLength) return false;
        var presented = request.Headers["X-Api-Key"].ToString();
        if (presented.Length == 0)
        {
            var authz = request.Headers.Authorization.ToString();
            presented = authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authz[7..] : "";
        }
        presented = presented.Trim();
        // Constant-time (hashing first equalises lengths).
        return presented.Length > 0 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
    }

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
