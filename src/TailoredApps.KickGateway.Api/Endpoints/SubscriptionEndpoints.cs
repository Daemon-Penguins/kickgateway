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
            KickGatewayDbContext db,
            BroadcasterTokenService tokens,
            IKickClient kick,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("KickEnroll");
            var account = await db.Broadcasters
                .Include(x => x.Subscriptions)
                .Include(x => x.KickClientApp)
                .FirstOrDefaultAsync(x => x.Id == broadcasterId, ct);
            if (account is null) return Results.NotFound(new { error = "broadcaster not found" });

            var token = await tokens.GetAccessTokenAsync(broadcasterId, ct);
            if (token is null) return Results.BadRequest(new { error = "no usable access token (refresh failed?)" });

            var results = new List<object>();
            foreach (var (eventName, version) in KickEventTypes.All)
            {
                if (account.Subscriptions.Any(s => s.EventType == eventName && s.Version == version))
                {
                    results.Add(new { eventName, version, status = "already_subscribed" });
                    continue;
                }
                var info = await kick.CreateSubscriptionAsync(token, eventName, version, "webhook", broadcasterUserId: null, ct);
                if (info is null)
                {
                    results.Add(new { eventName, version, status = "failed" });
                    continue;
                }
                account.Subscriptions.Add(new KickEventSubscription
                {
                    EventType = eventName,
                    Version = version,
                    Method = "webhook",
                    KickSubscriptionId = info.Id,
                });
                results.Add(new { eventName, version, status = "subscribed", subscriptionId = info.Id });
            }
            await db.SaveChangesAsync(ct);
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
