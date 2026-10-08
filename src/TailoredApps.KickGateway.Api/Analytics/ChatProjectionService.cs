using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>
/// One projection step per inbox: read the next batch after that inbox's checkpoint (keyset on
/// ReceivedAt + key), map it to chat read-model rows, insert what isn't there yet, and advance the
/// checkpoint — all in one SaveChanges transaction, so a crash mid-batch simply replays the batch.
/// Row keys are deterministic, so replays are no-ops.
/// </summary>
public static class ChatProjector
{
    public const string WebhookCheckpoint = "chat-projection:webhooks";
    public const string RealtimeCheckpoint = "chat-projection:realtime";

    /// <summary>Two copies of a message (one per inbox) count as the same message when sender and
    /// content match within this many seconds — only relevant if Kick's ids ever diverge between
    /// transports (they are the same UUID today, which the primary key already merges).</summary>
    public const int CrossSourceToleranceSeconds = 5;

    /// <param name="horizon">Only rows received strictly before this are read (see <see cref="ChatAnalyticsOptions.ProjectionOptions.LagSeconds"/>).</param>
    /// <returns>Number of inbox rows consumed (0 = caught up).</returns>
    public static async Task<int> ProjectWebhooksAsync(KickGatewayDbContext db, int batchSize, DateTime horizon, CancellationToken ct)
    {
        var cp = await CheckpointAsync(db, WebhookCheckpoint, ct);
        var t = cp.Position;
        var k = cp.PositionKey;
        var types = ChatProjectionMapper.ProjectedEventTypes;
        var rows = await db.ReceivedWebhooks.AsNoTracking()
            .Where(x => types.Contains(x.EventType)
                        && x.ReceivedAt < horizon
                        && (x.ReceivedAt > t || (x.ReceivedAt == t && string.Compare(x.MessageId, k) > 0)))
            .OrderBy(x => x.ReceivedAt).ThenBy(x => x.MessageId)
            .Take(batchSize)
            .Select(x => new ReceivedWebhook
            {
                MessageId = x.MessageId,
                EventType = x.EventType,
                BroadcasterAccountId = x.BroadcasterAccountId,
                ReceivedAt = x.ReceivedAt,
                RawBody = x.RawBody,
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        var accountIds = rows.Where(r => r.BroadcasterAccountId is not null).Select(r => r.BroadcasterAccountId!.Value).Distinct().ToList();
        var slugs = await db.Broadcasters.AsNoTracking()
            .Where(b => accountIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.ChannelSlug, ct);

        var batch = new Batch();
        foreach (var row in rows)
        {
            var slug = row.BroadcasterAccountId is { } id && slugs.TryGetValue(id, out var s) ? s : null;
            batch.Add(ChatProjectionMapper.Map(row, slug));
        }

        await SaveAsync(db, batch, ChatSource.Webhook, ct);
        Advance(cp, rows[^1].ReceivedAt, rows[^1].MessageId, rows.Count);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <inheritdoc cref="ProjectWebhooksAsync"/>
    public static async Task<int> ProjectRealtimeAsync(KickGatewayDbContext db, int batchSize, DateTime horizon, CancellationToken ct)
    {
        var cp = await CheckpointAsync(db, RealtimeCheckpoint, ct);
        var t = cp.Position;
        var k = cp.PositionKey;
        // No event-name filter in SQL: names are namespaced Laravel classes and irrelevant ones
        // (polls, chatroom modes, …) are cheap to skip in the mapper.
        var rows = await db.ReceivedRealtimeEvents.AsNoTracking()
            .Where(x => x.ReceivedAt < horizon
                        && (x.ReceivedAt > t || (x.ReceivedAt == t && string.Compare(x.DedupeKey, k) > 0)))
            .OrderBy(x => x.ReceivedAt).ThenBy(x => x.DedupeKey)
            .Take(batchSize)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        // Channels with an enabled webhook broadcaster: webhooks own their non-chat events.
        var webhookSlugs = (await db.Broadcasters.AsNoTracking()
                .Where(b => b.IsEnabled)
                .Select(b => b.ChannelSlug)
                .ToListAsync(ct))
            .Select(s => s.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var batch = new Batch();
        foreach (var row in rows)
            batch.Add(RealtimeChatProjectionMapper.Map(row, webhookSlugs.Contains((row.Slug ?? "").ToLowerInvariant())));

        await ResolveIdentitiesAsync(db, batch, ct);
        await SaveAsync(db, batch, ChatSource.Realtime, ct);
        Advance(cp, rows[^1].ReceivedAt, rows[^1].DedupeKey, rows.Count);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private sealed class Batch
    {
        // Case-insensitive like the SQL Server collation the keys are compared under.
        public readonly Dictionary<string, ChatMessageRecord> Messages = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ChatterEvent> Events = new(StringComparer.OrdinalIgnoreCase);

        public void Add(ChatProjection p)
        {
            foreach (var m in p.Messages) Messages.TryAdd(m.MessageId, m);
            foreach (var e in p.Events) Events.TryAdd(e.Id, e);
        }
    }

    private static async Task<AnalyticsCheckpoint> CheckpointAsync(KickGatewayDbContext db, string name, CancellationToken ct)
    {
        var cp = await db.AnalyticsCheckpoints.FirstOrDefaultAsync(x => x.Name == name, ct);
        if (cp is not null) return cp;
        cp = new AnalyticsCheckpoint { Name = name, Position = DateTime.MinValue, PositionKey = "" };
        db.AnalyticsCheckpoints.Add(cp);
        return cp;
    }

    private static void Advance(AnalyticsCheckpoint cp, DateTime position, string key, int count)
    {
        cp.Position = position;
        cp.PositionKey = key;
        cp.ProcessedCount += count;
        cp.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Pusher frames often name people by username only. Fill empty user ids from the chat we've
    /// seen (this batch first, then the stored messages), and the author of deleted messages from
    /// the message itself. Unresolvable rows keep the username and an empty id.
    /// </summary>
    private static async Task ResolveIdentitiesAsync(KickGatewayDbContext db, Batch batch, CancellationToken ct)
    {
        if (batch.Events.Count == 0) return;

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in batch.Messages.Values.OrderBy(m => m.CreatedAt))
            if (m.SenderUsername.Length > 0) byName[m.SenderUsername] = m.SenderUserId;

        var wanted = batch.Events.Values
            .SelectMany(e => new[]
            {
                e.UserId.Length == 0 ? e.Username : null,
                e.CounterpartUserId is null ? e.CounterpartUsername : null,
            })
            .Where(n => !string.IsNullOrEmpty(n) && !byName.ContainsKey(n!))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count > 0)
        {
            var found = await db.ChatMessages.AsNoTracking()
                .Where(m => wanted.Contains(m.SenderUsername))
                .GroupBy(m => m.SenderUsername)
                .Select(g => new { Name = g.Key, Id = g.OrderByDescending(m => m.CreatedAt).Select(m => m.SenderUserId).FirstOrDefault() })
                .ToListAsync(ct);
            foreach (var f in found)
                if (!string.IsNullOrEmpty(f.Id)) byName.TryAdd(f.Name, f.Id);
        }

        var deletedIds = batch.Events.Values
            .Where(e => e.Kind == ChatterEventKind.MessageDeleted && e.RefId is not null && !batch.Messages.ContainsKey(e.RefId))
            .Select(e => e.RefId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var authors = deletedIds.Count == 0
            ? new Dictionary<string, (string Id, string Name)>(StringComparer.OrdinalIgnoreCase)
            : (await db.ChatMessages.AsNoTracking()
                    .Where(m => deletedIds.Contains(m.MessageId))
                    .Select(m => new { m.MessageId, m.SenderUserId, m.SenderUsername })
                    .ToListAsync(ct))
                .ToDictionary(m => m.MessageId, m => (m.SenderUserId, m.SenderUsername), StringComparer.OrdinalIgnoreCase);

        foreach (var e in batch.Events.Values)
        {
            if (e.Kind == ChatterEventKind.MessageDeleted && e.RefId is not null)
            {
                if (batch.Messages.TryGetValue(e.RefId, out var msg)) (e.UserId, e.Username) = (msg.SenderUserId, msg.SenderUsername);
                else if (authors.TryGetValue(e.RefId, out var a)) (e.UserId, e.Username) = a;
                continue;
            }
            if (e.UserId.Length == 0 && e.Username.Length > 0 && byName.TryGetValue(e.Username, out var uid))
                e.UserId = uid;
            if (e.CounterpartUserId is null && e.CounterpartUsername is { Length: > 0 } cn && byName.TryGetValue(cn, out var cid))
                e.CounterpartUserId = cid;
        }
    }

    private static async Task SaveAsync(KickGatewayDbContext db, Batch batch, ChatSource source, CancellationToken ct)
    {
        if (batch.Messages.Count > 0)
        {
            var ids = batch.Messages.Keys.ToList();
            var existing = await db.ChatMessages.AsNoTracking().Where(m => ids.Contains(m.MessageId)).Select(m => m.MessageId).ToListAsync(ct);
            foreach (var id in existing) batch.Messages.Remove(id);
            await DropCrossSourceDuplicatesAsync(db, batch, source, ct);
            db.ChatMessages.AddRange(batch.Messages.Values);
        }
        if (batch.Events.Count > 0)
        {
            var ids = batch.Events.Keys.ToList();
            var existing = await db.ChatterEvents.AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct);
            foreach (var id in existing) batch.Events.Remove(id);
            db.ChatterEvents.AddRange(batch.Events.Values);
        }
    }

    private static async Task DropCrossSourceDuplicatesAsync(KickGatewayDbContext db, Batch batch, ChatSource source, CancellationToken ct)
    {
        var tolerance = TimeSpan.FromSeconds(CrossSourceToleranceSeconds);
        foreach (var group in batch.Messages.Values.GroupBy(m => m.ChannelSlug).ToList())
        {
            var slug = group.Key;
            var from = group.Min(m => m.CreatedAt) - tolerance;
            var to = group.Max(m => m.CreatedAt) + tolerance;
            var others = await db.ChatMessages.AsNoTracking()
                .Where(m => m.ChannelSlug == slug && m.Source != source && m.CreatedAt >= from && m.CreatedAt <= to)
                .Select(m => new { m.SenderUserId, m.Content, m.CreatedAt })
                .ToListAsync(ct);
            if (others.Count == 0) continue;

            var seen = others.ToLookup(o => (o.SenderUserId, o.Content));
            foreach (var m in group)
                if (seen[(m.SenderUserId, m.Content)].Any(o => (o.CreatedAt - m.CreatedAt).Duration() <= tolerance))
                    batch.Messages.Remove(m.MessageId);
        }
    }
}

/// <summary>
/// Keeps the chat read model (ChatMessages / ChatMentions / ChatterEvents) in step with both
/// inboxes (webhooks + realtime). On first start it backfills their whole history, then tails
/// them. Single instance assumed (the Api runs as one replica); a second instance would only waste
/// work — inserts are idempotent and a conflicting batch is retried.
/// </summary>
public sealed class ChatProjectionService(
    IServiceScopeFactory scopes,
    IOptions<ChatAnalyticsOptions> options,
    ILogger<ChatProjectionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value.Projection;
        if (!o.Enabled)
        {
            log.LogInformation("Chat analytics projection disabled (Analytics:Projection:Enabled=false)");
            return;
        }

        var batch = Math.Clamp(o.BatchSize, 50, 2000);
        var idle = TimeSpan.FromSeconds(Math.Max(1, o.PollSeconds));
        var lag = TimeSpan.FromSeconds(Math.Max(0, o.LagSeconds));

        while (!ct.IsCancellationRequested)
        {
            var busy = false;
            busy |= await StepAsync("webhooks", (db, h) => ChatProjector.ProjectWebhooksAsync(db, batch, h, ct), batch, lag, ct);
            if (o.IncludeRealtime)
                busy |= await StepAsync("realtime", (db, h) => ChatProjector.ProjectRealtimeAsync(db, batch, h, ct), batch, lag, ct);
            if (ct.IsCancellationRequested) break;

            if (!busy)
            {
                try { await Task.Delay(idle, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <returns>True when the batch was full (more rows are likely waiting).</returns>
    private async Task<bool> StepAsync(string source, Func<KickGatewayDbContext, DateTime, Task<int>> project,
        int batch, TimeSpan lag, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
            var processed = await project(db, DateTime.UtcNow - lag);
            if (processed > 0) log.LogDebug("Chat analytics projected {Count} {Source} inbox rows", processed, source);
            return processed >= batch;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Chat analytics projection of {Source} failed; retrying", source);
            return false;
        }
    }
}
