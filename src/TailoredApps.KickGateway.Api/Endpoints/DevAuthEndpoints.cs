using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Endpoints;

/// <summary>
/// Development-only local sign-in. Reaches <c>/admin</c> without a Kick developer app
/// or the OAuth round-trip — for local work and manual testing. It mints the exact same
/// <c>AdminCookie</c> the real OAuth callback does (via <see cref="AdminPrincipalFactory"/>).
///
/// Only ever mapped when the host environment is Development (see Program.cs), and it also
/// self-guards at runtime as defense in depth. NEVER reachable in Production.
/// </summary>
public static class DevAuthEndpoints
{
    public static IEndpointRouteBuilder MapDevAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        // GET so a plain <a href> / browser address bar works.
        // /api/auth/dev/login            → sign in as the seeded "superadmin"
        // /api/auth/dev/login?username=x → sign in as x (auto-provisioned SuperAdmin if new)
        routes.MapGet("/api/auth/dev/login", async (
            string? username,
            string? returnUrl,
            HttpContext http,
            IWebHostEnvironment env,
            KickGatewayDbContext db,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            if (!env.IsDevelopment())
                return Results.NotFound();

            var log = lf.CreateLogger("DevAuth");
            username = string.IsNullOrWhiteSpace(username) ? "superadmin" : username.Trim().ToLowerInvariant();

            var admin = await db.AdminUsers
                .Include(x => x.Roles)
                .FirstOrDefaultAsync(x => x.Username == username, ct);

            if (admin is null)
            {
                // Fresh dev DB / arbitrary handle → provision a full SuperAdmin so you can
                // do anything locally. To exercise a restricted role, pre-create the user
                // with the roles you want (admin UI or SQL) and dev-login as them instead —
                // existing users keep exactly the roles they already have.
                admin = new AdminUser
                {
                    KickUserId = "dev:" + username, // synthetic + namespaced → never collides with a real Kick id
                    Username = username,
                    IsEnabled = true,
                    Roles = { new AdminUserRole { Role = AdminRole.SuperAdmin } },
                };
                db.AdminUsers.Add(admin);
                log.LogWarning("DEV login auto-provisioned SuperAdmin '{Username}'", username);
            }

            if (!admin.IsEnabled)
                return Results.BadRequest(new { error = $"admin '{username}' is disabled" });

            admin.LastLoginAt = DateTime.UtcNow;
            admin.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            // fallbackNameId keeps the seeded bootstrap row's empty KickUserId intact so a
            // later real Kick OAuth login can still bootstrap it by username.
            await http.SignInAsync(
                AdminClaims.AuthenticationScheme,
                AdminPrincipalFactory.Build(admin, fallbackNameId: "dev:" + username),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14) });

            log.LogInformation("DEV login as '{Username}' (roles: {Roles})",
                username, admin.Roles.Count == 0 ? "none" : string.Join(", ", admin.Roles.Select(r => r.Role)));

            // Local redirects only — never bounce off-site via the query string.
            var dest = string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal)
                ? "/admin"
                : returnUrl;
            return Results.Redirect(dest);
        });

        return routes;
    }
}
