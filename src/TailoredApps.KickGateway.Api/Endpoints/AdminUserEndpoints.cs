using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Endpoints;

public static class AdminUserEndpoints
{
    public record CreateAdminRequest(string Username, string? KickUserId, string? Email);
    public record GrantRoleRequest(AdminRole Role, Guid? KickClientAppId);

    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/admins")
            .RequireAuthorization(AdminPolicies.SuperAdminOnly);

        grp.MapGet("/", async (KickGatewayDbContext db, CancellationToken ct) =>
        {
            var list = await db.AdminUsers
                .Include(x => x.Roles)
                .OrderBy(x => x.Username)
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        grp.MapPost("/", async (CreateAdminRequest req, KickGatewayDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username))
                return Results.BadRequest(new { error = "username required" });

            var u = new AdminUser
            {
                Username = req.Username.Trim(),
                KickUserId = req.KickUserId?.Trim() ?? "",
                Email = req.Email,
                IsEnabled = true,
            };
            db.AdminUsers.Add(u);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admins/{u.Id}", u);
        });

        grp.MapPatch("/{id:guid}/enable", async (Guid id, bool enabled, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var u = await db.AdminUsers.FindAsync(new object[] { id }, ct);
            if (u is null) return Results.NotFound();
            u.IsEnabled = enabled;
            u.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(u);
        });

        grp.MapPost("/{id:guid}/roles", async (Guid id, GrantRoleRequest req, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var u = await db.AdminUsers.Include(x => x.Roles).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u is null) return Results.NotFound();

            if (req.Role == AdminRole.SuperAdmin && req.KickClientAppId is not null)
                return Results.BadRequest(new { error = "SuperAdmin must be global (KickClientAppId=null)" });
            if (req.Role != AdminRole.SuperAdmin && req.KickClientAppId is null)
                return Results.BadRequest(new { error = "per-client role requires KickClientAppId" });

            if (req.KickClientAppId is { } cid)
            {
                var exists = await db.ClientApps.AnyAsync(c => c.Id == cid, ct);
                if (!exists) return Results.BadRequest(new { error = "client app not found" });
            }

            // Dedupe — same (user, client, role) is a no-op.
            if (u.Roles.Any(r => r.Role == req.Role && r.KickClientAppId == req.KickClientAppId))
                return Results.Ok(u);

            u.Roles.Add(new AdminUserRole
            {
                AdminUserId = u.Id,
                Role = req.Role,
                KickClientAppId = req.KickClientAppId,
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(u);
        });

        grp.MapDelete("/{id:guid}/roles/{roleId:guid}", async (Guid id, Guid roleId, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var role = await db.AdminUserRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.AdminUserId == id, ct);
            if (role is null) return Results.NotFound();

            // Never let the system end up with zero SuperAdmins.
            if (role.Role == AdminRole.SuperAdmin)
            {
                var remaining = await db.AdminUserRoles.CountAsync(r => r.Role == AdminRole.SuperAdmin, ct);
                if (remaining <= 1) return Results.BadRequest(new { error = "cannot remove the last SuperAdmin" });
            }

            db.AdminUserRoles.Remove(role);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return routes;
    }
}
