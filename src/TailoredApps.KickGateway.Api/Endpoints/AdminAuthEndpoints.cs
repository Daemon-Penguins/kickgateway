using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Services;

namespace TailoredApps.KickGateway.Api.Endpoints;

public static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Start: kick off PKCE against the KickClientApp marked IsAdminLoginClient.
        routes.MapGet("/api/auth/admin/login", async (
            string? returnUrl,
            KickGatewayDbContext db,
            PkceStateStore pkce,
            IOptions<KickGlobalDefaults> defaults,
            CancellationToken ct) =>
        {
            var app = await db.ClientApps
                .FirstOrDefaultAsync(x => x.IsAdminLoginClient && x.IsEnabled, ct);
            if (app is null) return Results.BadRequest(new { error = "no admin login client configured — mark a KickClientApp as IsAdminLoginClient" });

            // Flow tag carries the post-login redirect so we don't need a side
            // table or signed cookie. Sanitized to local paths only on callback.
            var flowTag = "admin:" + (string.IsNullOrEmpty(returnUrl) ? "/admin" : returnUrl);
            var challenge = PkceHelper.Create(flow: flowTag);
            await pkce.SaveAsync(challenge, app.Id, ct);

            var url = PkceHelper.BuildAuthorizationUrl(
                authBaseUrl: defaults.Value.AuthBaseUrl,
                clientId: app.ClientId,
                redirectUri: app.RedirectUri,
                scopes: "user:read",
                pkce: challenge);
            return Results.Redirect(url);
        });

        // Callback: exchange code, identify Kick user, sign in via cookie.
        routes.MapGet("/api/auth/admin/callback", async (
            string? code,
            string? state,
            string? error,
            HttpContext http,
            KickGatewayDbContext db,
            PkceStateStore pkce,
            IKickClient kick,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("AdminAuth");

            if (!string.IsNullOrEmpty(error))
                return Results.BadRequest(new { error });
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.BadRequest(new { error = "missing code or state" });

            var match = await pkce.TakeAsync(state, ct);
            if (match is null) return Results.BadRequest(new { error = "invalid or expired state" });
            var (entry, app) = match.Value;

            // Only the admin login client may serve admin OAuth — protects against
            // a malicious enrolled client app whose secret was leaked.
            if (!app.IsAdminLoginClient)
                return Results.BadRequest(new { error = "state was issued by a non-admin client" });

            var tokens = await kick.ExchangeAuthorizationCodeAsync(
                app.ClientId, app.ClientSecret, code!, entry.CodeVerifier, app.RedirectUri, ct);
            if (!tokens.Success || tokens.AccessToken is null)
                return Results.BadRequest(new { error = "token exchange failed", detail = tokens.Error });

            var kickUser = await kick.GetCurrentUserAsync(tokens.AccessToken, ct);
            if (kickUser is null || string.IsNullOrEmpty(kickUser.UserId))
                return Results.BadRequest(new { error = "could not resolve Kick user" });

            // Lookup by KickUserId; if no row, try bootstrap by username (seed path).
            var admin = await db.AdminUsers
                .Include(x => x.Roles)
                .FirstOrDefaultAsync(x => x.KickUserId == kickUser.UserId, ct);

            if (admin is null)
            {
                admin = await db.AdminUsers
                    .Include(x => x.Roles)
                    .FirstOrDefaultAsync(x => x.KickUserId == "" && x.Username == kickUser.Username, ct);
                if (admin is not null)
                {
                    admin.KickUserId = kickUser.UserId;
                    log.LogInformation("Bootstrap admin {Username} resolved to KickUserId={Id}", kickUser.Username, kickUser.UserId);
                }
            }

            if (admin is null || !admin.IsEnabled)
            {
                log.LogWarning("Admin login denied for KickUserId={Id} Username={U} — not in AdminUsers", kickUser.UserId, kickUser.Username);
                return Results.Forbid();
            }

            admin.Username = kickUser.Username;
            if (!string.IsNullOrEmpty(kickUser.Email)) admin.Email = kickUser.Email;
            admin.LastLoginAt = DateTime.UtcNow;
            admin.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var identity = new ClaimsIdentity(AdminClaims.AuthenticationScheme);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, admin.KickUserId));
            identity.AddClaim(new Claim(ClaimTypes.Name, admin.Username));
            identity.AddClaim(new Claim(AdminClaims.AdminUserId, admin.Id.ToString()));
            foreach (var r in admin.Roles)
            {
                if (r.Role == AdminRole.SuperAdmin)
                    identity.AddClaim(new Claim(AdminClaims.GlobalRole, nameof(AdminRole.SuperAdmin)));
                else if (r.KickClientAppId is { } cid)
                    identity.AddClaim(new Claim(AdminClaims.ClientRole, $"{cid}:{r.Role}"));
            }

            await http.SignInAsync(
                AdminClaims.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14) });

            var returnUrl = entry.Flow.StartsWith("admin:") ? entry.Flow[6..] : "/admin";
            // Only allow local redirects to avoid open-redirect via the state tag.
            if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
                returnUrl = "/admin";

            return Results.Redirect(returnUrl);
        });

        routes.MapPost("/api/auth/admin/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AdminClaims.AuthenticationScheme);
            return Results.Redirect("/admin");
        });

        // GET variant — convenient for a plain <a href> from the Razor nav.
        routes.MapGet("/api/auth/admin/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AdminClaims.AuthenticationScheme);
            return Results.Redirect("/admin");
        });

        return routes;
    }
}
