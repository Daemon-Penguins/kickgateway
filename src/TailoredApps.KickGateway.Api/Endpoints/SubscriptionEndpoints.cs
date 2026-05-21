using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Auth;
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
            ClaimsPrincipal user,
            KickGatewayDbContext db,
            SubscriptionEnrollmentService enroll,
            CancellationToken ct) =>
        {
            var (_, err) = await user.ResolveBroadcasterClientAsync(db, broadcasterId, AdminRole.ClientOperator, ct);
            if (err is not null) return err;

            var (ok, error, results) = await enroll.EnrollAllAsync(broadcasterId, ct);
            if (!ok) return error == "broadcaster not found"
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
            return Results.Ok(new { broadcasterId, results });
        });

        routes.MapGet("/api/subscriptions/{broadcasterId:guid}", async (
            Guid broadcasterId,
            ClaimsPrincipal user,
            KickGatewayDbContext db,
            BroadcasterTokenService tokens,
            IKickClient kick,
            CancellationToken ct) =>
        {
            var (_, err) = await user.ResolveBroadcasterClientAsync(db, broadcasterId, AdminRole.ClientViewer, ct);
            if (err is not null) return err;

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
            ClaimsPrincipal user,
            KickGatewayDbContext db,
            BroadcasterTokenService tokens,
            IKickClient kick,
            CancellationToken ct) =>
        {
            var (_, err) = await user.ResolveBroadcasterClientAsync(db, broadcasterId, AdminRole.ClientAdmin, ct);
            if (err is not null) return err;

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
