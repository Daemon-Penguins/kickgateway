using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.Integrations.Kick.Models;
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
        var accountExists = await _db.Broadcasters
            .AsNoTracking()
            .AnyAsync(x => x.Id == broadcasterId, ct);
        if (!accountExists) return (false, "broadcaster not found", Array.Empty<EnrollResult>());

        // Read existing subscriptions as a flat projection, no nav-collection
        // tracking. The earlier .Include(x => x.Subscriptions) + collection.Add
        // path caused EF to flip the new rows to Modified state instead of Added
        // (the broadcaster was already tracked from BroadcasterTokenService, so
        // re-loading via Include reused the tracked principal and EF treated the
        // appended children as updates of phantom rows → DbUpdateConcurrencyException
        // because UPDATE … WHERE [Id]=@p affected 0 rows).
        var existing = await _db.EventSubscriptions
            .AsNoTracking()
            .Where(s => s.KickBroadcasterAccountId == broadcasterId)
            .Select(s => new { s.EventType, s.Version })
            .ToListAsync(ct);
        var alreadyHave = existing.Select(s => (s.EventType, s.Version)).ToHashSet();

        var token = await _tokens.GetAccessTokenAsync(broadcasterId, ct);
        if (token is null) return (false, "no usable access token (refresh failed?)", Array.Empty<EnrollResult>());

        // Reconcile with Kick's remote list before issuing fresh POSTs — protects
        // against the case where a previous enroll succeeded on Kick's side but
        // the local SaveChanges failed (leaving Kick-side orphans without a local
        // mirror row). Without this, a re-run would create duplicate subscriptions.
        IReadOnlyList<KickSubscriptionInfo> remote;
        try { remote = await _kick.ListSubscriptionsAsync(token, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not list remote subscriptions before enroll for {Id} — proceeding without reconciliation", broadcasterId);
            remote = Array.Empty<KickSubscriptionInfo>();
        }
        var remoteByKey = remote
            .Where(r => int.TryParse(r.Version, out _))
            .ToDictionary(r => (r.Event, int.Parse(r.Version)), r => r);

        var results = new List<EnrollResult>();
        foreach (var (eventName, version) in KickEventTypes.All)
        {
            if (alreadyHave.Contains((eventName, version)))
            {
                results.Add(new(eventName, version, "already_subscribed"));
                continue;
            }
            // Kick already has this subscription but our DB doesn't — backfill the
            // local row instead of POSTing again. This is what cleans up after a
            // half-failed prior run.
            if (remoteByKey.TryGetValue((eventName, version), out var orphan))
            {
                _db.EventSubscriptions.Add(new KickEventSubscription
                {
                    KickBroadcasterAccountId = broadcasterId,
                    EventType = eventName,
                    Version = version,
                    Method = orphan.Method,
                    KickSubscriptionId = orphan.Id,
                });
                results.Add(new(eventName, version, "backfilled_from_kick", orphan.Id));
                continue;
            }
            var info = await _kick.CreateSubscriptionAsync(token, eventName, version, "webhook", broadcasterUserId: null, ct);
            if (info is null)
            {
                results.Add(new(eventName, version, "failed"));
                continue;
            }
            _db.EventSubscriptions.Add(new KickEventSubscription
            {
                KickBroadcasterAccountId = broadcasterId,
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
