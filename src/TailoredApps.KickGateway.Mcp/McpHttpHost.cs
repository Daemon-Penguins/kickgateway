using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Mcp.OAuth;

namespace TailoredApps.KickGateway.Mcp;

/// <summary>
/// The HTTP transport (`--http` / Mcp__Transport=http): Streamable HTTP at /mcp behind Traefik. Two ways in, both
/// ending at the same tools: the analytics API key (Bearer or X-Api-Key — scripts, Claude Code) or an access token
/// from the embedded OAuth server (ChatGPT, Claude.ai and other clients that only speak OAuth). Fails closed: no
/// usable key configured = every /mcp request is rejected.
/// </summary>
public static partial class McpHttpHost
{
    [GeneratedRegex(@"[\x00-\x1F\x7F]+")]
    private static partial Regex ControlChars { get; }

    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        McpComposition.AddGateway(builder.Services, builder.Configuration);
        McpComposition.AddKickMcp(builder.Services).WithHttpTransport(o => o.Stateless = true);

        builder.Services.Configure<McpHttpOptions>(builder.Configuration.GetSection(McpHttpOptions.SectionName));
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient(HttpClientMetadataDocuments.HttpClientName, http =>
            {
                http.Timeout = TimeSpan.FromSeconds(5);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("kickgateway-mcp/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        builder.Services.AddSingleton<IClientMetadataDocuments, HttpClientMetadataDocuments>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => new OAuthServer(
            sp.GetRequiredService<IOptions<McpHttpOptions>>().Value,
            ClientApiKey(sp.GetRequiredService<IConfiguration>()),
            sp.GetRequiredService<IClientMetadataDocuments>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<OAuthServer>>()));
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<McpHttpOptions>>().Value;
        var oauth = app.Services.GetRequiredService<OAuthServer>();
        var clientKey = ClientApiKey(app.Configuration);
        if (clientKey.Length < McpHttpOptions.MinClientKeyLength)
            app.Logger.LogWarning("No client API key of at least {Min} chars configured — every /mcp request will be rejected", McpHttpOptions.MinClientKeyLength);
        if (oauth.Active)
            app.Logger.LogInformation("OAuth enabled: metadata at /.well-known/oauth-protected-resource, consent at {Authorize} (public base URL: {Base})",
                OAuthEndpoints.AuthorizePath, string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? "derived from the request" : options.PublicBaseUrl);
        else
            app.Logger.LogInformation("OAuth disabled ({Reason}); /mcp accepts the API key only", options.OAuth.Enabled ? "no usable client key" : "Mcp:OAuth:Enabled=false");

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        app.Use(async (ctx, next) =>
        {
            if (!IsProtected(ctx.Request.Path) || IsApiKey(ctx.Request, clientKey))
            {
                await next();
                return;
            }

            var urls = OAuthUrls.For(ctx.Request, options.PublicBaseUrl);
            var bearer = BearerToken(ctx.Request);
            var problem = "";
            if (oauth.Active && bearer is not null && oauth.TryValidateAccessToken(bearer, urls, out problem))
            {
                await next();
                return;
            }

            var presented = bearer is not null || ctx.Request.Headers.ContainsKey("X-Api-Key");
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = Challenge(oauth.Active ? urls : null, presented ? "invalid_token" : null,
                presented ? (problem.Length > 0 ? "access token rejected: " + problem : "invalid API key") : null);
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "invalid_token",
                error_description = presented ? "the API key or access token was not accepted" : "authentication required",
            });
        });

        if (oauth.Active) app.MapMcpOAuth(oauth);
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>Everything under /mcp except the OAuth endpoints themselves.</summary>
    public static bool IsProtected(PathString path) => path.StartsWithSegments("/mcp") && !OAuthEndpoints.IsOAuthPath(path);

    public static string ClientApiKey(IConfiguration config) =>
        (config["Mcp:ClientApiKey"] is { Length: > 0 } k ? k : config["KickGateway:ApiKey"] ?? "").Trim();

    public static string? BearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && authorization.Length > 7
            ? authorization[7..].Trim()
            : null;
    }

    /// <summary>The API key, as X-Api-Key or Bearer. A missing/short configured key rejects everyone.</summary>
    public static bool IsApiKey(HttpRequest request, string configured)
    {
        if (configured.Length < McpHttpOptions.MinClientKeyLength) return false;
        var presented = request.Headers["X-Api-Key"].ToString().Trim();
        if (presented.Length == 0) presented = BearerToken(request) ?? "";
        // Constant-time (hashing first equalises lengths).
        return presented.Length > 0 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
    }

    /// <summary>RFC 6750 / RFC 9728 challenge: where the metadata is, which scope to ask for, and why the token failed.</summary>
    public static string Challenge(OAuthUrls? urls, string? error, string? description)
    {
        var parts = new List<string>(4);
        if (error is not null) parts.Add($"error=\"{error}\"");
        if (description is not null) parts.Add($"error_description=\"{ControlChars.Replace(description.Replace('"', '\''), " ")}\"");
        if (urls is not null)
        {
            parts.Add($"resource_metadata=\"{urls.ResourceMetadata}\"");
            parts.Add($"scope=\"{OAuthServer.Scope}\"");
        }
        return parts.Count == 0 ? "Bearer" : "Bearer " + string.Join(", ", parts);
    }
}
