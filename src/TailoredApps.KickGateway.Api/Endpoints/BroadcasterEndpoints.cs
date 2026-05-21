using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Endpoints;

public static class BroadcasterEndpoints
{
    public static IEndpointRouteBuilder MapBroadcasterEndpoints(this IEndpointRouteBuilder routes)
    {
        // List filtered to the client apps the user can see. SuperAdmin sees all.
        routes.MapGet("/api/broadcasters", async (ClaimsPrincipal user, KickGatewayDbContext db, CancellationToken ct) =>
        {
            IQueryable<KickBroadcasterAccount> q = db.Broadcasters
                .Include(x => x.KickClientApp)
                .Include(x => x.Subscriptions);
            if (!user.IsSuperAdmin())
            {
                var allowed = user.AccessibleClientAppIds();
                q = q.Where(x => allowed.Contains(x.KickClientAppId));
            }
            var list = await q.OrderBy(x => x.Username).ToListAsync(ct);
            return Results.Ok(list);
        });

        routes.MapGet("/api/broadcasters/{id:guid}", async (Guid id, ClaimsPrincipal user, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var (_, err) = await user.ResolveBroadcasterClientAsync(db, id, AdminRole.ClientViewer, ct);
            if (err is not null) return err;

            var b = await db.Broadcasters
                .Include(x => x.KickClientApp)
                .Include(x => x.Subscriptions)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            return b is null ? Results.NotFound() : Results.Ok(b);
        });

        routes.MapPatch("/api/broadcasters/{id:guid}/enable", async (Guid id, bool enabled, ClaimsPrincipal user, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var (_, err) = await user.ResolveBroadcasterClientAsync(db, id, AdminRole.ClientAdmin, ct);
            if (err is not null) return err;

            var b = await db.Broadcasters.FindAsync(new object[] { id }, ct);
            if (b is null) return Results.NotFound();
            b.IsEnabled = enabled;
            b.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(b);
        });

        return routes;
    }
}
