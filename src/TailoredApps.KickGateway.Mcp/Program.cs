using TailoredApps.KickGateway.Mcp;

// MCP server for Kick chat analytics — a thin adapter: every tool is one GET against the gateway's
// /api/analytics/* endpoints, authenticated with the analytics API key. Two transports:
//  • stdio (default): launched by a local MCP client (Claude Code / Desktop). stdout belongs to the protocol,
//    so all logging goes to stderr.
//  • HTTP (`--http` or Mcp__Transport=http): Streamable HTTP at /mcp — the `mcp` container behind Traefik
//    (see McpHttpHost). Clients authenticate with the API key (Bearer / X-Api-Key) or with an OAuth 2.1 access
//    token from the embedded authorization server, whose consent page asks for that same key.

var useHttp = args.Contains("--http")
              || string.Equals(Environment.GetEnvironmentVariable("Mcp__Transport"), "http", StringComparison.OrdinalIgnoreCase);
if (useHttp) await McpHttpHost.Build(args).RunAsync();
else await RunStdioAsync(args);

static async Task RunStdioAsync(string[] args)
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

    McpComposition.AddGateway(builder.Services, builder.Configuration);
    McpComposition.AddKickMcp(builder.Services).WithStdioServerTransport();
    await builder.Build().RunAsync();
}
