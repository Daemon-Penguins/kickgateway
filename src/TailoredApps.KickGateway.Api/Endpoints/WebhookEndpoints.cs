using System.Globalization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Webhooks;

namespace TailoredApps.KickGateway.Api.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/webhooks/kick", async (
            HttpContext http,
            IKickSignatureVerifier verifier,
            KickGatewayDbContext db,
            KickWebhookDispatcher dispatcher,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("KickWebhook");

            http.Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(http.Request.Body, leaveOpen: true))
                body = await reader.ReadToEndAsync(ct);
            http.Request.Body.Position = 0;

            var headers = http.Request.Headers;
            var messageId = headers[KickEventTypes.HeaderMessageId].ToString();
            var timestamp = headers[KickEventTypes.HeaderMessageTimestamp].ToString();
            var signature = headers[KickEventTypes.HeaderSignature].ToString();
            var subscriptionId = headers[KickEventTypes.HeaderSubscriptionId].ToString();
            var eventType = headers[KickEventTypes.HeaderEventType].ToString();
            var eventVersion = headers[KickEventTypes.HeaderEventVersion].ToString();

            if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(eventType))
            {
                log.LogWarning("Webhook missing required Kick-Event-* headers");
                return Results.BadRequest("missing required Kick-Event headers");
            }

            var verified = await verifier.VerifyAsync(messageId, timestamp, body, signature, ct);
            if (!verified)
            {
                log.LogWarning("Kick webhook signature verification failed (messageId={Mid})", messageId);
                return Results.StatusCode(401);
            }

            // Idempotency check: if we've already seen this message id, ack and skip.
            // (Insert path also catches duplicate inserts via the PK constraint.)
            var alreadySeen = await db.ReceivedWebhooks.AsNoTracking()
                .AnyAsync(x => x.MessageId == messageId, ct);
            if (alreadySeen)
            {
                log.LogInformation("Duplicate webhook {Mid} — already published, acking", messageId);
                return Results.Ok();
            }

            // Resolve the broadcaster the event is for. Kick puts the broadcaster
            // user id inside the payload under a "broadcaster" object on most
            // events, sometimes under different shapes. Pull it defensively.
            var broadcasterUserId = TryExtractBroadcasterUserId(body);
            var broadcaster = await db.Broadcasters
                .Include(x => x.KickClientApp)
                .FirstOrDefaultAsync(x => x.KickUserId == broadcasterUserId, ct);

            var kickHeaders = SerializeKickHeaders(headers);

            if (broadcaster is null)
            {
                log.LogWarning("Received {Type} webhook for unknown broadcaster_user_id={Bid} (mid={Mid}) — recording and dropping",
                    eventType, broadcasterUserId, messageId);
                db.ReceivedWebhooks.Add(new ReceivedWebhook
                {
                    MessageId = messageId,
                    EventType = eventType,
                    SubscriptionId = subscriptionId,
                    BroadcasterAccountId = null,
                    ReceivedAt = DateTime.UtcNow,
                    PublishedAt = null,
                    RawBody = body,
                    Headers = kickHeaders
                });
                await db.SaveChangesAsync(ct);
                return Results.Ok();
            }

            DateTime? kickTs = DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
            var receivedAt = DateTime.UtcNow;

            // Insert the inbox row + publish in a single transaction. MassTransit's
            // EF outbox holds the published message until the transaction commits,
            // so the row and the queued publish appear atomically.
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            db.ReceivedWebhooks.Add(new ReceivedWebhook
            {
                MessageId = messageId,
                EventType = eventType,
                SubscriptionId = subscriptionId,
                BroadcasterAccountId = broadcaster.Id,
                ReceivedAt = receivedAt,
                PublishedAt = receivedAt,
                RawBody = body,
                Headers = kickHeaders
            });

            try
            {
                await dispatcher.PublishAsync(eventType, eventVersion, messageId, subscriptionId, kickTs, receivedAt, broadcaster, body, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // raced with another worker on the same delivery — duplicate is fine
                await tx.RollbackAsync(ct);
                log.LogInformation("Duplicate insert race on {Mid} — acking", messageId);
            }

            return Results.Ok();
        });

        return routes;
    }

    private static string TryExtractBroadcasterUserId(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("broadcaster", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (b.TryGetProperty("user_id", out var uid))
                    return uid.ValueKind == System.Text.Json.JsonValueKind.Number ? uid.GetRawText() : (uid.GetString() ?? "");
            }
            if (root.TryGetProperty("broadcaster_user_id", out var direct))
                return direct.ValueKind == System.Text.Json.JsonValueKind.Number ? direct.GetRawText() : (direct.GetString() ?? "");
            return "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Pulls just the Kick-Event-* headers plus Content-Type. Skips user-agent, CDN
    /// noise, and anything that could carry secrets we don't need long-term.
    /// </summary>
    private static string SerializeKickHeaders(IHeaderDictionary headers)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in headers)
        {
            if (!key.StartsWith("Kick-Event-", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(key).Append(": ").Append(value.ToString()).Append('\n');
        }
        return sb.ToString();
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // SQL Server surfaces 2627 (PK) / 2601 (unique index) via SqlException.Number.
        var inner = ex.InnerException;
        if (inner is Microsoft.Data.SqlClient.SqlException sql)
            return sql.Number is 2627 or 2601;
        return inner != null && (inner.Message.Contains("duplicate key") || inner.Message.Contains("UNIQUE"));
    }
}
