using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Contracts.Realtime;

namespace TailoredApps.KickGateway.Realtime.Ingest;

/// <summary>
/// Records + publishes a realtime event with the same inbox+outbox guarantee as the webhook
/// path (<c>KickWebhookDispatcher</c>): a <see cref="ReceivedRealtimeEvent"/> dedupe row and
/// the MassTransit publish commit together via the EF bus outbox, then the delivery service
/// ships the message to RabbitMQ. A duplicate key (Pusher redelivers on reconnect) rolls the
/// whole thing back — no double publish.
/// </summary>
public class RealtimeIngestService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RealtimeIngestService> _log;

    public RealtimeIngestService(IServiceScopeFactory scopes, ILogger<RealtimeIngestService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task IngestAsync(IKickRealtimeEvent evt, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        db.ReceivedRealtimeEvents.Add(new ReceivedRealtimeEvent
        {
            DedupeKey = evt.DedupeKey,
            EventName = evt.PusherEvent,
            PusherChannel = evt.PusherChannel,
            Slug = evt.BroadcasterSlug,
            ChannelId = string.IsNullOrEmpty(evt.KickChannelId) ? null : evt.KickChannelId,
            ChatroomId = evt.KickChatroomId,
            ReceivedAt = evt.ReceivedAt,
            RawData = evt.RawData,
            PublishedAt = DateTime.UtcNow,
        });

        // Cast to object so MassTransit publishes the CONCRETE contract type (its own exchange),
        // not the IKickRealtimeEvent interface. This enlists into the scoped EF bus outbox.
        await publish.Publish((object)evt, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            _log.LogDebug("Duplicate realtime frame {Key} ({Event}) — skipped", evt.DedupeKey, evt.PusherEvent);
        }
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        // SQL Server: 2627 = PK violation, 2601 = unique index violation.
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SqlException sql && sql.Number is 2627 or 2601)
                return true;
        return false;
    }
}
