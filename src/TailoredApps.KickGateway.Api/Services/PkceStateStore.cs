using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Kick.Models;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Services;

/// <summary>
/// Persists PKCE challenges across the OAuth roundtrip. We DB-back them so a
/// horizontally scaled gateway can finish a flow on whichever instance receives
/// the callback.
/// </summary>
public class PkceStateStore
{
    private readonly KickGatewayDbContext _db;
    public PkceStateStore(KickGatewayDbContext db) { _db = db; }

    public async Task SaveAsync(PkceChallenge pkce, Guid clientAppId, CancellationToken ct = default)
    {
        _db.PkceStates.Add(new PkceStateEntry
        {
            State = pkce.State,
            CodeVerifier = pkce.CodeVerifier,
            CodeChallenge = pkce.CodeChallenge,
            ExpiresAt = pkce.ExpiresAt,
            Flow = pkce.Flow,
            KickClientAppId = clientAppId,
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(PkceStateEntry Entry, KickClientApp App)?> TakeAsync(string state, CancellationToken ct = default)
    {
        var entry = await _db.PkceStates.FirstOrDefaultAsync(x => x.State == state, ct);
        if (entry is null) return null;
        var app = await _db.ClientApps.FirstOrDefaultAsync(x => x.Id == entry.KickClientAppId, ct);
        if (app is null) { _db.PkceStates.Remove(entry); await _db.SaveChangesAsync(ct); return null; }
        _db.PkceStates.Remove(entry);
        await _db.SaveChangesAsync(ct);
        if (entry.ExpiresAt < DateTime.UtcNow) return null;
        return (entry, app);
    }
}
