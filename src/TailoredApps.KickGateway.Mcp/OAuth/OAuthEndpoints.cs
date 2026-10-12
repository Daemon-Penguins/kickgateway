using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace TailoredApps.KickGateway.Mcp.OAuth;

/// <summary>
/// HTTP surface of <see cref="OAuthServer"/>: the two metadata documents (served at every path a client might probe),
/// dynamic registration, the consent page and the token endpoint. The well-known paths sit at the host root, so the
/// reverse proxy must route them to this container (see deploy.yml); everything else lives under /mcp/oauth.
/// </summary>
public static class OAuthEndpoints
{
    public const string AuthorizePath = "/mcp/oauth/authorize";
    public const string TokenPath = "/mcp/oauth/token";
    public const string RegisterPath = "/mcp/oauth/register";

    private static readonly string[] ResourceMetadataPaths =
    [
        "/.well-known/oauth-protected-resource",        // root form
        "/.well-known/oauth-protected-resource/mcp",    // RFC 9728 path form for the resource https://host/mcp
        "/mcp/.well-known/oauth-protected-resource",    // path-appended fallback some clients try
    ];

    private static readonly string[] ServerMetadataPaths =
    [
        "/.well-known/oauth-authorization-server",
        "/.well-known/openid-configuration",
        "/mcp/.well-known/oauth-authorization-server",
        "/mcp/.well-known/openid-configuration",
    ];

    private const int MaxRegistrationBytes = 16 * 1024;
    private static readonly TimeSpan WrongKeyDelay = TimeSpan.FromSeconds(1);

    /// <summary>True for the OAuth endpoints themselves, which must not sit behind the /mcp token check.</summary>
    public static bool IsOAuthPath(PathString path) =>
        path.StartsWithSegments("/mcp/oauth") || path.StartsWithSegments("/mcp/.well-known") || path.StartsWithSegments("/.well-known");

    public static void MapMcpOAuth(this IEndpointRouteBuilder app, OAuthServer oauth)
    {
        foreach (var path in ResourceMetadataPaths)
            app.MapGet(path, (HttpContext ctx, IOptions<McpHttpOptions> o) => Metadata(ctx, oauth.ProtectedResourceMetadata(Urls(ctx, o))));
        foreach (var path in ServerMetadataPaths)
            app.MapGet(path, (HttpContext ctx, IOptions<McpHttpOptions> o) => Metadata(ctx, oauth.AuthorizationServerMetadata(Urls(ctx, o))));

        if (oauth.DynamicRegistrationEnabled) app.MapPost(RegisterPath, RegisterAsync);
        app.MapGet(AuthorizePath, AuthorizeGetAsync);
        app.MapPost(AuthorizePath, AuthorizePostAsync);
        app.MapPost(TokenPath, TokenAsync);
    }

    private static OAuthUrls Urls(HttpContext ctx, IOptions<McpHttpOptions> o) => OAuthUrls.For(ctx.Request, o.Value.PublicBaseUrl);

    private static IResult Metadata(HttpContext ctx, JsonObject document)
    {
        ctx.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Json(document);
    }

    private static async Task<IResult> RegisterAsync(HttpContext ctx, OAuthServer oauth, CancellationToken ct)
    {
        NoStore(ctx);
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxRegistrationBytes;
        if (ctx.Request.ContentLength > MaxRegistrationBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        JsonObject? body;
        try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct) as JsonObject; }
        catch (JsonException) { body = null; }
        catch (BadHttpRequestException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }

        var result = oauth.Register(body);
        return result.Error is { } e ? Results.Json(e.ToJson(), statusCode: e.Status) : Results.Json(result.Value, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> AuthorizeGetAsync(HttpContext ctx, OAuthServer oauth, IOptions<McpHttpOptions> o, CancellationToken ct)
    {
        NoStore(ctx);
        var request = ReadAuthorize(k => ctx.Request.Query[k]);
        var urls = Urls(ctx, o);
        return await oauth.ValidateAuthorizeAsync(request, urls, ct) switch
        {
            AuthorizeOutcome.Rejected r => ErrorPage(ctx, r.Error),
            AuthorizeOutcome.RedirectWithError e => RedirectError(e.RedirectUri, e.Error, e.State, urls),
            AuthorizeOutcome.Valid v => ConsentPage(ctx, v.Client, request, error: null, StatusCodes.Status200OK),
            var other => throw new InvalidOperationException(other.GetType().Name),
        };
    }

    private static async Task<IResult> AuthorizePostAsync(HttpContext ctx, OAuthServer oauth, IOptions<McpHttpOptions> o, CancellationToken ct)
    {
        NoStore(ctx);
        if (!ctx.Request.HasFormContentType) return Results.BadRequest();
        var form = await ctx.Request.ReadFormAsync(ct);
        var request = ReadAuthorize(k => form[k]);
        var urls = Urls(ctx, o);

        switch (await oauth.ValidateAuthorizeAsync(request, urls, ct))
        {
            case AuthorizeOutcome.Rejected r:
                return ErrorPage(ctx, r.Error);
            case AuthorizeOutcome.RedirectWithError e:
                return RedirectError(e.RedirectUri, e.Error, e.State, urls);
            case AuthorizeOutcome.Valid v:
                if (form["decision"] == "deny")
                    return RedirectError(v.RedirectUri, new OAuthError("access_denied", "the user denied the request"), v.State, urls);
                if (!oauth.CheckUserCredential(form["api_key"]))
                {
                    await Task.Delay(WrongKeyDelay, ct);
                    return ConsentPage(ctx, v.Client, request, "That key was not accepted. Paste the gateway's analytics API key (ANALYTICS_API_KEY).", StatusCodes.Status401Unauthorized);
                }
                var code = oauth.IssueCode(v, urls);
                return Results.Redirect(QueryHelpers.AddQueryString(v.RedirectUri, new Dictionary<string, string?>
                {
                    ["code"] = code,
                    ["state"] = v.State,
                    ["iss"] = urls.Issuer, // RFC 9207
                }));
            default:
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> TokenAsync(HttpContext ctx, OAuthServer oauth, IOptions<McpHttpOptions> o, CancellationToken ct)
    {
        NoStore(ctx);
        if (!ctx.Request.HasFormContentType)
            return TokenError(ctx, new OAuthError("invalid_request", "the token request must be application/x-www-form-urlencoded"));
        var form = await ctx.Request.ReadFormAsync(ct);

        string? clientId = Value(form["client_id"]), clientSecret = Value(form["client_secret"]);
        var authorization = ctx.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBasic(authorization[6..], out var basicId, out var basicSecret))
                return TokenError(ctx, new OAuthError("invalid_client", "malformed Basic credentials", 401));
            if (clientId is not null && clientId != basicId)
                return TokenError(ctx, new OAuthError("invalid_request", "client_id in the body and in the Authorization header differ"));
            clientId = basicId;
            clientSecret = basicSecret;
        }

        var result = await oauth.TokenAsync(new TokenRequest(
            Value(form["grant_type"]), clientId, clientSecret, Value(form["code"]), Value(form["code_verifier"]),
            Value(form["redirect_uri"]), Value(form["refresh_token"]), Value(form["resource"])), Urls(ctx, o), ct);
        return result.Error is { } e ? TokenError(ctx, e) : Results.Json(result.Value);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────────────────────────────────────

    private static IResult ConsentPage(HttpContext ctx, OAuthClient client, AuthorizeRequest request, string? error, int status)
    {
        PageHeaders(ctx);
        var hidden = new StringBuilder();
        foreach (var (name, value) in new (string, string?)[]
                 {
                     ("client_id", request.ClientId), ("redirect_uri", request.RedirectUri), ("response_type", request.ResponseType),
                     ("state", request.State), ("scope", request.Scope), ("code_challenge", request.CodeChallenge),
                     ("code_challenge_method", request.CodeChallengeMethod), ("resource", request.Resource),
                 })
        {
            if (value is not null) hidden.Append($"<input type=\"hidden\" name=\"{name}\" value=\"{H(value)}\">");
        }

        var warning = client.HasLoopbackRedirect
            ? "<p class=\"warn\">This client redirects to a localhost address. Only approve if you started this connection yourself.</p>"
            : "";
        var errorHtml = error is null ? "" : $"<p class=\"error\">{H(error)}</p>";

        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Connect to Kick chat analytics</title>
            <style>
            body{font:16px/1.5 system-ui,sans-serif;background:#0f1115;color:#e6e6e6;margin:0;display:flex;min-height:100vh;align-items:center;justify-content:center}
            main{background:#181b22;border:1px solid #2a2f3a;border-radius:12px;padding:28px 32px;max-width:460px;width:100%}
            h1{font-size:20px;margin:0 0 12px}p{margin:8px 0}.muted{color:#9aa3b2;font-size:14px}.warn{color:#f5c542}.error{color:#ff6b6b}
            label{display:block;margin:18px 0 6px;font-weight:600}input[type=password]{width:100%;box-sizing:border-box;padding:10px 12px;border-radius:8px;border:1px solid #3a4150;background:#0f1115;color:#fff;font-size:16px}
            .actions{display:flex;gap:12px;margin-top:18px}button{flex:1;padding:10px 14px;border-radius:8px;border:0;font-size:16px;cursor:pointer}
            .approve{background:#53fc18;color:#0f1115;font-weight:700}.deny{background:#2a2f3a;color:#e6e6e6}
            </style>
            </head>
            <body>
            <main>
            <h1>Kick chat analytics</h1>
            <p><strong>{{H(client.Name)}}</strong> <span class="muted">({{H(client.RedirectHosts)}})</span> asks for read access to the chat analytics (scope <code>analytics</code>).</p>
            {{warning}}
            {{errorHtml}}
            <form method="post" action="{{AuthorizePath}}">
            {{hidden}}
            <label for="api_key">Analytics API key</label>
            <input id="api_key" name="api_key" type="password" autocomplete="off" autofocus required>
            <div class="actions">
            <button class="approve" name="decision" value="approve">Approve</button>
            <button class="deny" name="decision" value="deny" formnovalidate>Deny</button>
            </div>
            </form>
            <p class="muted">The key is the gateway's <code>ANALYTICS_API_KEY</code>. Approving gives this client its own tokens; the key itself is not stored or shared with it.</p>
            </main>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }

    private static IResult ErrorPage(HttpContext ctx, OAuthError error)
    {
        PageHeaders(ctx);
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>Authorization error</title>
            <style>body{font:16px/1.5 system-ui,sans-serif;background:#0f1115;color:#e6e6e6;padding:40px;max-width:600px;margin:auto}code{color:#ff6b6b}</style>
            </head><body><h1>Authorization error</h1><p><code>{{H(error.Error)}}</code>: {{H(error.Description)}}</p>
            <p>The request did not identify a trusted client, so it was not redirected anywhere.</p></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: error.Status);
    }

    private static IResult RedirectError(string redirectUri, OAuthError error, string? state, OAuthUrls urls) =>
        Results.Redirect(QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
        {
            ["error"] = error.Error,
            ["error_description"] = error.Description,
            ["state"] = state,
            ["iss"] = urls.Issuer,
        }));

    private static IResult TokenError(HttpContext ctx, OAuthError error)
    {
        if (error.Status == 401) ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"mcp\"";
        return Results.Json(error.ToJson(), statusCode: error.Status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private static AuthorizeRequest ReadAuthorize(Func<string, StringValues> get) => new(
        Value(get("client_id")), Value(get("redirect_uri")), Value(get("response_type")), Value(get("state")), Value(get("scope")),
        Value(get("code_challenge")), Value(get("code_challenge_method")), Value(get("resource")));

    private static string? Value(StringValues values) => values.Count > 0 && values[0] is { Length: > 0 } v ? v : null;

    private static bool TryParseBasic(string encoded, out string clientId, out string clientSecret)
    {
        clientId = clientSecret = "";
        try
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
            var colon = pair.IndexOf(':');
            if (colon < 0) return false;
            clientId = Uri.UnescapeDataString(pair[..colon]);
            clientSecret = Uri.UnescapeDataString(pair[(colon + 1)..]);
            return clientId.Length > 0;
        }
        catch (FormatException) { return false; }
    }

    private static void NoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";
    }

    private static void PageHeaders(HttpContext ctx)
    {
        NoStore(ctx);
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers.XFrameOptions = "DENY";
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
}
