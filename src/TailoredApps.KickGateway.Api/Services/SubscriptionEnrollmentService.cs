using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Services;

/// <summary>
/// Subscribes a broadcaster to every event in <see cref="KickEventTypes.All"/>
/// via Kick's <c>/public/v1/events/subscriptions</c> endpoint. Idempotent —
/// rows that already exist locally are reported as <c>already_subscribed</c>
/// without hitting Kick. Used by both the REST endpoint and the Blazor admin
/// page (the page calls in-process to avoid a 10×-Kick-POST HTTP self-loopback
/// that overflowed the 30s standard resilience handler timeout).
/// </summary>
public class SubscriptionEnrollmentService
{
    private readonly KickGatewayDbContext _db;
    private readonly BroadcasterTokenService _tokens;
    private readonly IKickClient _kick;
    private readonly ILogger<SubscriptionEnrollmentService> _log;

    public SubscriptionEnrollmentService(KickGatewayDbContext db, BroadcasterTokenService tokens, IKickClient kick, ILogger<SubscriptionEnrollmentService> log)
    {
        _db = db;
        _tokens = tokens;
        _kick = kick;
        _log = log;
    }

    public record EnrollResult(string EventName, int Version, string Status, string? SubscriptionId = null);

    public async Task<(bool ok, string? error, IReadOnlyList<EnrollResult> results)> EnrollAllAsync(Guid broadcasterId, CancellationToken ct = default)
    {
        var account = await _db.Broadcasters
            .Include(x => x.Subscriptions)
            .Include(x => x.KickClientApp)
            .FirstOrDefaultAsync(x => x.Id == broadcasterId, ct);
        if (account is null) return (false, "broadcaster not found", Array.Empty<EnrollResult>());

        var token = await _tokens.GetAccessTokenAsync(broadcasterId, ct);
        if (token is null) return (false, "no usable access token (refresh failed?)", Array.Empty<EnrollResult>());

        var results = new List<EnrollResult>();
        foreach (var (eventName, version) in KickEventTypes.All)
        {
            if (account.Subscriptions.Any(s => s.EventType == eventName && s.Version == version))
            {
                results.Add(new(eventName, version, "already_subscribed"));
                continue;
            }
            var info = await _kick.CreateSubscriptionAsync(token, eventName, version, "webhook", broadcasterUserId: null, ct);
            if (info is null)
            {
                results.Add(new(eventName, version, "failed"));
                continue;
            }
            account.Subscriptions.Add(new KickEventSubscription
            {
                EventType = eventName,
                Version = version,
                Method = "webhook",
                KickSubscriptionId = info.Id,
            });
            results.Add(new(eventName, version, "subscribed", info.Id));
        }
        await _db.SaveChangesAsync(ct);
        return (true, null, results);
    }
}
