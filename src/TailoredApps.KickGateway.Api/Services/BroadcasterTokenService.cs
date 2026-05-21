using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Services;

/// <summary>
/// Resolves a usable access token for a broadcaster. Refreshes proactively when
/// the cached token is within the configurable headroom window of expiry.
/// </summary>
public class BroadcasterTokenService
{
    private static readonly TimeSpan RefreshHeadroom = TimeSpan.FromMinutes(5);

    private readonly KickGatewayDbContext _db;
    private readonly IKickClient _kick;
    private readonly ILogger<BroadcasterTokenService> _log;

    public BroadcasterTokenService(KickGatewayDbContext db, IKickClient kick, ILogger<BroadcasterTokenService> log)
    {
        _db = db;
        _kick = kick;
        _log = log;
    }

    public async Task<string?> GetAccessTokenAsync(Guid broadcasterId, CancellationToken ct = default)
    {
        var account = await _db.Broadcasters
            .Include(x => x.KickClientApp)
            .FirstOrDefaultAsync(x => x.Id == broadcasterId, ct);
        if (account is null || account.KickClientApp is null) return null;
        if (!account.IsEnabled) return null;

        if (account.AccessTokenExpiresAt - DateTime.UtcNow > RefreshHeadroom && !string.IsNullOrEmpty(account.AccessToken))
            return account.AccessToken;

        return await RefreshAsync(account, ct);
    }

    public async Task<string?> RefreshAsync(KickBroadcasterAccount account, CancellationToken ct = default)
    {
        if (account.KickClientApp is null)
            throw new InvalidOperationException($"Broadcaster {account.Id} missing client app navigation");

        var result = await _kick.RefreshAccessTokenAsync(
            account.KickClientApp.ClientId,
            account.KickClientApp.ClientSecret,
            account.RefreshToken,
            ct);

        if (!result.Success || result.AccessToken is null || result.RefreshToken is null)
        {
            _log.LogError("Refresh failed for broadcaster {Id}: {Err}", account.Id, result.Error);
            return null;
        }

        account.AccessToken = result.AccessToken;
        account.RefreshToken = result.RefreshToken;
        account.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, result.ExpiresInSec - 30));
        account.LastRefreshedAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(result.Scope)) account.Scopes = result.Scope;
        await _db.SaveChangesAsync(ct);
        return account.AccessToken;
    }
}
