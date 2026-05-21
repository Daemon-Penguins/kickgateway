using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Auth;

/// <summary>
/// Endpoint-side per-client authorization helpers. We can't express
/// "you need ClientAdmin on the client owning broadcaster X" with a static
/// policy alone — the broadcaster→client lookup is per-request.
/// </summary>
public static class AccessChecks
{
    public static async Task<IResult?> RequireClientRoleAsync(
        this ClaimsPrincipal user, KickGatewayDbContext db, Guid clientAppId,
        AdminRole minimum, CancellationToken ct = default)
    {
        if (user.HasClientRoleAtLeast(clientAppId, minimum)) return null;
        // 403 not 401 — they're logged in, they just lack the role.
        return Results.Forbid();
    }

    /// <summary>
    /// Resolves a broadcaster's client-app id and checks the user's role on it.
    /// Returns (clientAppId, null) on success or (Guid.Empty, errorResult) on failure.
    /// </summary>
    public static async Task<(Guid ClientAppId, IResult? Error)> ResolveBroadcasterClientAsync(
        this ClaimsPrincipal user, KickGatewayDbContext db, Guid broadcasterId,
        AdminRole minimum, CancellationToken ct = default)
    {
        var clientAppId = await db.Broadcasters
            .Where(x => x.Id == broadcasterId)
            .Select(x => (Guid?)x.KickClientAppId)
            .FirstOrDefaultAsync(ct);
        if (clientAppId is null) return (Guid.Empty, Results.NotFound(new { error = "broadcaster not found" }));
        if (!user.HasClientRoleAtLeast(clientAppId.Value, minimum)) return (Guid.Empty, Results.Forbid());
        return (clientAppId.Value, null);
    }
}
