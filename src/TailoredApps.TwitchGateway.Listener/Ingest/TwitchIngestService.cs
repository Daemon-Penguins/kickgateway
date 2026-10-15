using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.TwitchGateway.Contracts;

namespace TailoredApps.TwitchGateway.Listener.Ingest;

/// <summary>
/// Records one Twitch event in the inbox (<see cref="ReceivedTwitchEvent"/>) and publishes its typed
/// contract through the EF transactional outbox in the same SaveChanges, so the row and the RabbitMQ
/// publish commit together. A duplicate (EventSub redelivery after a reconnect, Helix state seen twice)
/// trips the primary key and is acknowledged without a publish.
/// </summary>
public sealed class TwitchIngestService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TwitchIngestService> _log;

    public TwitchIngestService(IServiceScopeFactory scopes, ILogger<TwitchIngestService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task<TwitchIngestOutcome> IngestAsync(ITwitchEvent evt, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        db.ReceivedTwitchEvents.Add(new ReceivedTwitchEvent
        {
            DedupeKey = evt.DedupeKey,
            EventType = evt.EventType,
            EventVersion = evt.EventVersion,
            BroadcasterUserId = evt.BroadcasterUserId,
            BroadcasterLogin = evt.BroadcasterLogin,
            ReceivedAt = evt.ReceivedAt,
            TwitchTimestamp = evt.TwitchTimestamp,
            RawData = evt.RawPayload,
            PublishedAt = DateTime.UtcNow,
        });

        // The cast makes MassTransit publish the concrete type (its own exchange), enlisted in the scoped bus outbox.
        await publish.Publish((object)evt, ct);

        try
        {
            await db.SaveChangesAsync(ct);
            return TwitchIngestOutcome.Published;
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            _log.LogDebug("Duplicate Twitch event {Key} on {Channel} - skipped", evt.DedupeKey, evt.BroadcasterLogin);
            return TwitchIngestOutcome.Duplicate;
        }
    }

    // SQL Server: 2627 = unique constraint, 2601 = unique index.
    private static bool IsDuplicateKey(DbUpdateException ex) => ex.InnerException is SqlException { Number: 2601 or 2627 };
}
