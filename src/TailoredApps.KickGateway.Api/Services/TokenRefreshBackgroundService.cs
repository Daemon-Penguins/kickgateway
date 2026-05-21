using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Services;

/// <summary>
/// Periodically refreshes tokens that are within 10 minutes of expiry. Belt
/// and braces — <see cref="BroadcasterTokenService.GetAccessTokenAsync"/>
/// already refreshes on demand, but the background pass keeps tokens warm for
/// outbound calls that happen far from any webhook traffic.
/// </summary>
public class TokenRefreshBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);

    private readonly IServiceProvider _sp;
    private readonly ILogger<TokenRefreshBackgroundService> _log;

    public TokenRefreshBackgroundService(IServiceProvider sp, ILogger<TokenRefreshBackgroundService> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // small initial delay so EF migrations finish first
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Token refresh pass failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { }
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<BroadcasterTokenService>();

        var cutoff = DateTime.UtcNow.Add(Threshold);
        var due = await db.Broadcasters
            .Include(x => x.KickClientApp)
            .Where(x => x.IsEnabled && x.AccessTokenExpiresAt < cutoff)
            .ToListAsync(ct);

        foreach (var account in due)
        {
            await tokens.RefreshAsync(account, ct);
        }
    }
}
