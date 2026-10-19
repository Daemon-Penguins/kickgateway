using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>Row counts of the two landing-page tables, as of <see cref="At"/>.</summary>
public sealed record StoredCounts(long ChatMessages, long Transcripts, DateTime At);

/// <summary>
/// "Messages / transcripts stored so far" for the public landing page. The tables are large and the page is
/// public and polled, so this never runs <c>COUNT(*)</c>: it reads the row count SQL Server keeps per
/// partition (<c>sys.partitions</c> - instant, a catalog view every login can read for tables it can access) and
/// caches the pair for <see cref="Ttl"/> so any number of open pages cost one query per interval.
/// </summary>
public sealed class StoredTotals
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StoredTotals> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StoredCounts? _cached;

    public StoredTotals(IServiceScopeFactory scopes, ILogger<StoredTotals> log)
    {
        _scopes = scopes;
        _log = log;
    }

    /// <summary>Cached counts; null only while the very first query has not succeeded yet.</summary>
    public async ValueTask<StoredCounts?> GetAsync(CancellationToken ct = default)
    {
        var cached = _cached;
        if (cached is not null && DateTime.UtcNow - cached.At < Ttl) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            cached = _cached;
            if (cached is not null && DateTime.UtcNow - cached.At < Ttl) return cached;

            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
            var messages = await RowCountAsync(db, typeof(ChatMessageRecord), ct);
            var transcripts = await RowCountAsync(db, typeof(LiveTranscriptRecord), ct);
            return _cached = new StoredCounts(messages, transcripts, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not read stored totals; serving the previous value");
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<long> RowCountAsync(KickGatewayDbContext db, Type entity, CancellationToken ct)
    {
        var type = db.Model.FindEntityType(entity)
                   ?? throw new InvalidOperationException($"{entity.Name} is not mapped");
        var table = $"[{type.GetSchema() ?? "dbo"}].[{type.GetTableName()}]";
        // index_id 0 = heap, 1 = clustered index: the one copy of every row. The aggregate is exactly one row
        // (Single, not First: EF warns about First without an OrderBy).
        return await db.Database
            .SqlQuery<long>($"SELECT CAST(ISNULL(SUM(p.[rows]), 0) AS bigint) AS [Value] FROM sys.partitions p WHERE p.[object_id] = OBJECT_ID({table}) AND p.index_id IN (0, 1)")
            .SingleAsync(ct);
    }
}
