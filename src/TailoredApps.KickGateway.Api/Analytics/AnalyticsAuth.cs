using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>
/// Auth for <c>/api/analytics/*</c>: the admin cookie (browser) OR a shared API key (machine
/// clients such as the MCP server). A policy scheme picks the handler per request — a request
/// carrying a key is judged by the key alone, so challenges come back as 401 instead of the
/// cookie's login redirect. The key principal is only accepted by the analytics policy; every
/// other admin endpoint keeps authenticating with the cookie scheme only.
/// </summary>
public static class AnalyticsAuth
{
    public const string Policy = "AnalyticsRead";
    public const string SelectorScheme = "AnalyticsCookieOrKey";
    public const string ApiKeyScheme = "AnalyticsApiKey";
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Claim marking a principal that may read analytics for every channel.</summary>
    public const string ScopeClaim = "analytics_scope";
    public const string ScopeAll = "all";

    public static AuthenticationBuilder AddAnalyticsApiKey(this AuthenticationBuilder auth)
    {
        auth.AddPolicyScheme(SelectorScheme, "Admin cookie or analytics API key", o =>
            o.ForwardDefaultSelector = ctx => HasKey(ctx.Request) ? ApiKeyScheme : AdminClaims.AuthenticationScheme);
        auth.AddScheme<AuthenticationSchemeOptions, AnalyticsApiKeyHandler>(ApiKeyScheme, null);
        return auth;
    }

    public static AuthorizationOptions AddAnalyticsPolicy(this AuthorizationOptions o)
    {
        o.AddPolicy(Policy, p => p
            .AddAuthenticationSchemes(SelectorScheme)
            .RequireAuthenticatedUser());
        return o;
    }

    internal static bool HasKey(HttpRequest request) =>
        request.Headers.ContainsKey(ApiKeyHeader)
        || request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    internal static string? ReadKey(HttpRequest request)
    {
        var header = request.Headers[ApiKeyHeader].ToString();
        if (header.Length > 0) return header.Trim();
        var authz = request.Headers.Authorization.ToString();
        return authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authz[7..].Trim() : null;
    }

    /// <summary>Constant-time comparison (hashing first equalises lengths).</summary>
    public static bool KeysMatch(string presented, string configured) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
}

public sealed class AnalyticsApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<ChatAnalyticsOptions> analytics)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = AnalyticsAuth.ReadKey(Request);
        if (string.IsNullOrEmpty(presented)) return Task.FromResult(AuthenticateResult.NoResult());

        var configured = analytics.CurrentValue.ApiKey?.Trim() ?? "";
        if (configured.Length < ChatAnalyticsOptions.MinApiKeyLength)
        {
            Logger.LogWarning("Analytics API key presented but key access is disabled (Analytics:ApiKey unset or shorter than {Min} chars)",
                ChatAnalyticsOptions.MinApiKeyLength);
            return Task.FromResult(AuthenticateResult.Fail("analytics API key access is disabled"));
        }
        if (!AnalyticsAuth.KeysMatch(presented, configured))
            return Task.FromResult(AuthenticateResult.Fail("invalid analytics API key"));

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "analytics-api-key"),
            new Claim(AnalyticsAuth.ScopeClaim, AnalyticsAuth.ScopeAll),
        }, AnalyticsAuth.ApiKeyScheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

/// <summary>
/// Which channels a caller may analyse. SuperAdmins and the API key see every channel; other
/// admins see the channels of broadcasters under client apps they hold any role on.
/// </summary>
public sealed class AnalyticsScope
{
    public static readonly AnalyticsScope Unrestricted = new(null);

    private AnalyticsScope(IReadOnlyCollection<string>? slugs) => Slugs = slugs;

    /// <summary>Allowed slugs (lowercase), or null for "all channels".</summary>
    public IReadOnlyCollection<string>? Slugs { get; }

    public bool Allows(string slug) => Slugs is null || Slugs.Contains(slug.ToLowerInvariant());

    public static AnalyticsScope ForSlugs(IEnumerable<string> slugs) =>
        new(slugs.Select(s => s.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal));

    public static async Task<AnalyticsScope> ForUserAsync(ClaimsPrincipal user, KickGatewayDbContext db, CancellationToken ct)
    {
        if (user.HasClaim(AnalyticsAuth.ScopeClaim, AnalyticsAuth.ScopeAll) || user.IsSuperAdmin())
            return Unrestricted;

        var clients = user.AccessibleClientAppIds().ToList();
        if (clients.Count == 0) return ForSlugs(Array.Empty<string>());
        var slugs = await db.Broadcasters.AsNoTracking()
            .Where(b => clients.Contains(b.KickClientAppId))
            .Select(b => b.ChannelSlug)
            .Distinct()
            .ToListAsync(ct);
        return ForSlugs(slugs);
    }
}
