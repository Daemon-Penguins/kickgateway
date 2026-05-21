using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Services;

namespace TailoredApps.KickGateway.Api.Endpoints;

public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder routes)
    {
        // Enroll a broadcaster in all KickEventTypes.All events with method=webhook.
        // Idempotent: skips events that already have a row in EventSubscriptions.
        routes.MapPost("/api/subscriptions/{broadcasterId:guid}/enroll", async (
            Guid broadcasterId,
            SubscriptionEnrollmentService enroll,
            CancellationToken ct) =>
        {
            var (ok, error, results) = await enroll.EnrollAllAsync(broadcasterId, ct);
            if (!ok) return error == "broadcaster not found"
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
            return Results.Ok(new { broadcasterId, results });
        });

        routes.MapGet("/api/subscriptions/{broadcasterId:guid}", async (
            Guid broadcasterId,
            KickGatewayDbContext db,
            BroadcasterTokenService tokens,
            IKickClient kick,
            CancellationToken ct) =>
        {
            var account = await db.Broadcasters.Include(x => x.Subscriptions).FirstOrDefaultAsync(x => x.Id == broadcasterId, ct);
            if (account is null) return Results.NotFound();

            var token = await tokens.GetAccessTokenAsync(broadcasterId, ct);
            if (token is null) return Results.Ok(new { remote = Array.Empty<object>(), local = account.Subscriptions });

            var remote = await kick.ListSubscriptionsAsync(token, ct);
            return Results.Ok(new { remote, local = account.Subscriptions });
        });

        routes.MapDelete("/api/subscriptions/{broadcasterId:guid}/{subscriptionId}", async (
            Guid broadcasterId,
            string subscriptionId,
            KickGatewayDbContext db,
            BroadcasterTokenService tokens,
            IKickClient kick,
            CancellationToken ct) =>
        {
            var token = await tokens.GetAccessTokenAsync(broadcasterId, ct);
            if (token is null) return Results.BadRequest(new { error = "no usable token" });
            var ok = await kick.DeleteSubscriptionAsync(token, subscriptionId, ct);
            if (ok)
            {
                var row = await db.EventSubscriptions.FirstOrDefaultAsync(
                    x => x.KickBroadcasterAccountId == broadcasterId && x.KickSubscriptionId == subscriptionId, ct);
                if (row is not null) { db.EventSubscriptions.Remove(row); await db.SaveChangesAsync(ct); }
            }
            return ok ? Results.Ok() : Results.BadRequest();
        });

        return routes;
    }
}
