using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Endpoints;

public static class BroadcasterEndpoints
{
    public static IEndpointRouteBuilder MapBroadcasterEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/broadcasters", async (KickGatewayDbContext db, CancellationToken ct) =>
        {
            var list = await db.Broadcasters
                .Include(x => x.KickClientApp)
                .Include(x => x.Subscriptions)
                .OrderBy(x => x.Username)
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        routes.MapGet("/api/broadcasters/{id:guid}", async (Guid id, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var b = await db.Broadcasters
                .Include(x => x.KickClientApp)
                .Include(x => x.Subscriptions)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            return b is null ? Results.NotFound() : Results.Ok(b);
        });

        routes.MapPatch("/api/broadcasters/{id:guid}/enable", async (Guid id, bool enabled, KickGatewayDbContext db, CancellationToken ct) =>
        {
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
