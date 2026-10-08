using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>
/// Wraps every <c>/api/analytics/*</c> request:
/// <list type="bullet">
/// <item>Runs its queries in a <see cref="IsolationLevel.ReadUncommitted"/> transaction. The endpoints are
/// read-only aggregates over a derived read model, so dirty reads are harmless, while shared locks were
/// not: during a backfill the projector inserts batch after batch into heavily indexed tables, and scans
/// taking shared locks ended up as deadlock victims (HTTP 500 after ~5 s, the deadlock monitor interval).
/// Without shared locks the analytics reads can neither block nor deadlock the projector or the webhook /
/// realtime ingest writers.</item>
/// <item>Turns failures into JSON: transient database errors (deadlock, timeout, lock timeout) → 503 with a
/// retry hint; anything else → 500 with the trace id, logged.</item>
/// </list>
/// </summary>
public sealed class AnalyticsRequestFilter(KickGatewayDbContext db, ILogger<AnalyticsRequestFilter> log) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        try
        {
            // Handlers materialise their results before returning, so the transaction covers every query.
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadUncommitted, ct);
            return await next(context);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsTransient(ex))
        {
            log.LogWarning(ex, "Analytics request {Path} hit a transient database error", http.Request.Path);
            http.Response.Headers.RetryAfter = "2";
            return Results.Json(new { error = "database busy (deadlock or timeout) — retry in a moment", traceId = TraceId(http) },
                AnalyticsJson.Options, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Analytics request {Path} failed", http.Request.Path);
            return Results.Json(new { error = "internal error", traceId = TraceId(http) },
                AnalyticsJson.Options, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>SQL Server deadlock victim (1205), lock request timeout (1222), command timeout (-2) or a timeout anywhere in the chain.</summary>
    public static bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && sql.Errors.Cast<SqlError>().Any(x => x.Number is 1205 or 1222 or -2)) return true;
            if (e is TimeoutException) return true;
        }
        return false;
    }

    private static string TraceId(HttpContext http) => Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
}
