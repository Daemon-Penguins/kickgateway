using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Endpoints;

public static class ClientAppEndpoints
{
    public record CreateClientAppRequest(string Name, string ClientId, string ClientSecret, string RedirectUri, string? Scopes, string? WebhookUrl);
    public record UpdateClientAppRequest(string? Name, string? ClientId, string? ClientSecret, string? RedirectUri, string? Scopes, string? WebhookUrl, bool? IsEnabled, bool? IsAdminLoginClient);

    public static IEndpointRouteBuilder MapClientAppEndpoints(this IEndpointRouteBuilder routes)
    {
        // Anyone authenticated sees the list (filtered for non-SuperAdmin to
        // the apps they have any role on). Secret is included — only
        // SuperAdmin reaches here in practice because non-SAs don't manage clients,
        // but defense-in-depth: blank the secret for everyone but SuperAdmin.
        routes.MapGet("/api/clients", async (ClaimsPrincipal user, KickGatewayDbContext db, CancellationToken ct) =>
        {
            IQueryable<KickClientApp> q = db.ClientApps;
            if (!user.IsSuperAdmin())
            {
                var allowed = user.AccessibleClientAppIds();
                q = q.Where(x => allowed.Contains(x.Id));
            }
            var list = await q.OrderBy(x => x.Name).ToListAsync(ct);
            if (!user.IsSuperAdmin())
                foreach (var c in list) c.ClientSecret = "";
            return Results.Ok(list);
        });

        // Create / delete / IsAdminLoginClient toggle / secret edits — SuperAdmin only.
        routes.MapPost("/api/clients", async (CreateClientAppRequest req, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var app = new KickClientApp
            {
                Name = req.Name,
                ClientId = req.ClientId,
                ClientSecret = req.ClientSecret,
                RedirectUri = req.RedirectUri,
                Scopes = string.IsNullOrWhiteSpace(req.Scopes) ? "events:subscribe channel:read channel:write user:read chat:write streamkey:read moderation:ban" : req.Scopes,
                WebhookUrl = req.WebhookUrl ?? "",
            };
            db.ClientApps.Add(app);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/clients/{app.Id}", app);
        }).RequireAuthorization(AdminPolicies.SuperAdminOnly);

        routes.MapPut("/api/clients/{id:guid}", async (Guid id, UpdateClientAppRequest req, ClaimsPrincipal user, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var app = await db.ClientApps.FindAsync(new object[] { id }, ct);
            if (app is null) return Results.NotFound();

            // Non-SuperAdmins need ClientAdmin on the row and can't touch secrets/auth toggles.
            if (!user.IsSuperAdmin())
            {
                if (!user.HasClientRoleAtLeast(id, AdminRole.ClientAdmin)) return Results.Forbid();
                if (req.ClientSecret is not null || req.ClientId is not null || req.IsAdminLoginClient.HasValue)
                    return Results.Forbid();
            }

            if (req.Name is not null) app.Name = req.Name;
            if (req.ClientId is not null) app.ClientId = req.ClientId;
            if (req.ClientSecret is not null) app.ClientSecret = req.ClientSecret;
            if (req.RedirectUri is not null) app.RedirectUri = req.RedirectUri;
            if (req.Scopes is not null) app.Scopes = req.Scopes;
            if (req.WebhookUrl is not null) app.WebhookUrl = req.WebhookUrl;
            if (req.IsEnabled.HasValue) app.IsEnabled = req.IsEnabled.Value;
            if (req.IsAdminLoginClient.HasValue)
            {
                // Enforce single-flag invariant: clear it elsewhere first.
                if (req.IsAdminLoginClient.Value)
                {
                    var others = await db.ClientApps.Where(c => c.IsAdminLoginClient && c.Id != id).ToListAsync(ct);
                    foreach (var o in others) o.IsAdminLoginClient = false;
                }
                app.IsAdminLoginClient = req.IsAdminLoginClient.Value;
            }
            app.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(app);
        });

        routes.MapDelete("/api/clients/{id:guid}", async (Guid id, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var app = await db.ClientApps.FindAsync(new object[] { id }, ct);
            if (app is null) return Results.NotFound();
            db.ClientApps.Remove(app);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AdminPolicies.SuperAdminOnly);

        return routes;
    }
}
