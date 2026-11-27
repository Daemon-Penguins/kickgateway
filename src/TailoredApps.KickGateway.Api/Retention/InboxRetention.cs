using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Retention;

/// <summary>Configuration section <c>InboxRetention</c>.</summary>
public sealed class InboxRetentionOptions
{
    public const string SectionName = "InboxRetention";

    /// <summary>Run the inbox purge in this process.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Inbox rows received more than this many days ago are deleted (minimum 1).</summary>
    public int Days { get; set; } = 7;

    /// <summary>Rows per DELETE. Kept well under SQL Server's 5000-lock escalation threshold so a
    /// purge never takes a table lock that would stall the ingest writers.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Pause between two batches, so a large backlog is drained without hogging the log or the disk.</summary>
    public int PauseMilliseconds { get; set; } = 100;

    /// <summary>Delay between two purge runs.</summary>
    public int IntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Deletes old rows from the three ingest inboxes (<c>ReceivedWebhooks</c>, <c>ReceivedRealtimeEvents</c>,
/// <c>ReceivedTwitchEvents</c>). They hold the verbatim payloads, are only needed for dedupe (minutes) and
/// as the source of the chat read model, and were 60 % of the prod database. A row the projector has
/// not consumed yet is never deleted: when the projector reads an inbox, the cutoff is clamped to that
/// inbox's checkpoint, and an inbox it reads but has no checkpoint for is left alone.
/// </summary>
public static class InboxRetention
{
    /// <summary>The cutoff for one inbox: rows with <c>ReceivedAt</c> strictly before it may go; null = delete nothing.</summary>
    /// <param name="checkpoint">The projector's checkpoint for this inbox, when the projector reads it.</param>
    public static DateTime? Cutoff(DateTime now, int days, bool projected, AnalyticsCheckpoint? checkpoint)
    {
        var byAge = now - TimeSpan.FromDays(Math.Max(1, days));
        if (!projected) return byAge;
        if (checkpoint is null) return null;
        // Strictly before Position: rows AT Position may still be partly unread (keyset ties on the key).
        return checkpoint.Position < byAge ? checkpoint.Position : byAge;
    }

    /// <summary>One purge run over every inbox.</summary>
    /// <returns>Rows deleted per inbox.</returns>
    public static async Task<IReadOnlyDictionary<string, long>> PurgeAsync(
        Func<KickGatewayDbContext> openDb, InboxRetentionOptions retention, ChatAnalyticsOptions.ProjectionOptions projection,
        DateTime now, CancellationToken ct)
    {
        var batch = Math.Clamp(retention.BatchSize, 100, 4000);
        var pause = TimeSpan.FromMilliseconds(Math.Max(0, retention.PauseMilliseconds));

        Dictionary<string, AnalyticsCheckpoint> checkpoints;
        await using (var db = openDb())
            checkpoints = await db.AnalyticsCheckpoints.AsNoTracking().ToDictionaryAsync(c => c.Name, ct);

        DateTime? CutoffFor(bool projected, string name) =>
            Cutoff(now, retention.Days, projection.Enabled && projected, checkpoints.GetValueOrDefault(name));

        var deleted = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["webhooks"] = await DrainAsync(openDb, CutoffFor(true, ChatProjector.WebhookCheckpoint), batch, pause,
                (db, c, n) => db.ReceivedWebhooks.Where(x => x.ReceivedAt < c).Take(n).ExecuteDeleteAsync(ct), ct),
            ["realtime"] = await DrainAsync(openDb, CutoffFor(projection.IncludeRealtime, ChatProjector.RealtimeCheckpoint), batch, pause,
                (db, c, n) => db.ReceivedRealtimeEvents.Where(x => x.ReceivedAt < c).Take(n).ExecuteDeleteAsync(ct), ct),
            ["twitch"] = await DrainAsync(openDb, CutoffFor(projection.IncludeTwitch, ChatProjector.TwitchCheckpoint), batch, pause,
                (db, c, n) => db.ReceivedTwitchEvents.Where(x => x.ReceivedAt < c).Take(n).ExecuteDeleteAsync(ct), ct),
        };
        return deleted;
    }

    private static async Task<long> DrainAsync(Func<KickGatewayDbContext> openDb, DateTime? cutoff, int batch, TimeSpan pause,
        Func<KickGatewayDbContext, DateTime, int, Task<int>> deleteBatch, CancellationToken ct)
    {
        if (cutoff is not { } c) return 0;
        long total = 0;
        while (!ct.IsCancellationRequested)
        {
            int n;
            // A fresh context per batch: each DELETE is its own short transaction.
            await using (var db = openDb())
                n = await deleteBatch(db, c, batch);
            total += n;
            if (n < batch) break;
            if (pause > TimeSpan.Zero) await Task.Delay(pause, ct);
        }
        return total;
    }
}

/// <summary>Runs <see cref="InboxRetention.PurgeAsync"/> shortly after start and then every <see cref="InboxRetentionOptions.IntervalMinutes"/>.</summary>
public sealed class InboxRetentionService(
    IServiceScopeFactory scopes,
    IOptions<InboxRetentionOptions> retention,
    IOptions<ChatAnalyticsOptions> analytics,
    ILogger<InboxRetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = retention.Value;
        if (!o.Enabled)
        {
            log.LogInformation("Inbox retention disabled (InboxRetention:Enabled=false)");
            return;
        }

        // Let the Api finish starting (migrations, projector warm-up) before the first run.
        if (!await DelayAsync(TimeSpan.FromMinutes(1), ct)) return;
        var interval = TimeSpan.FromMinutes(Math.Max(1, o.IntervalMinutes));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KickGatewayDbContext>>();
                var started = DateTime.UtcNow;
                var deleted = await InboxRetention.PurgeAsync(factory.CreateDbContext, o, analytics.Value.Projection, started, ct);
                log.LogInformation(
                    "Inbox retention ({Days} d): deleted {Webhooks} webhook, {Realtime} realtime, {Twitch} twitch rows in {Elapsed}",
                    Math.Max(1, o.Days), deleted["webhooks"], deleted["realtime"], deleted["twitch"], DateTime.UtcNow - started);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Inbox retention run failed; retrying next interval");
            }

            if (!await DelayAsync(interval, ct)) break;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
