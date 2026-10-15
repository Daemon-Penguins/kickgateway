using Microsoft.EntityFrameworkCore;
using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.TwitchGateway.Listener.Roster;

/// <summary>One channel the listener follows: resolved ids plus the capture flags from the admin page.</summary>
public record ManagedTwitchChannel(string Login, string UserId, string DisplayName, bool CaptureChat, bool CaptureAudio);

/// <summary>
/// Reads the enabled <see cref="TwitchChannel"/> rows and resolves logins without a cached user id via
/// Helix (persisting the id, display name and resolve time back onto the row). Shared by the EventSub
/// session manager and the stream-state poller; the audio capture has its own copy of the same logic.
/// </summary>
public sealed class TwitchRosterProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ITwitchHelixClient _helix;
    private readonly TwitchAccessTokenSource _tokens;
    private readonly ILogger<TwitchRosterProvider> _log;

    public TwitchRosterProvider(IServiceScopeFactory scopes, ITwitchHelixClient helix, TwitchAccessTokenSource tokens, ILogger<TwitchRosterProvider> log)
    {
        _scopes = scopes;
        _helix = helix;
        _tokens = tokens;
        _log = log;
    }

    public async Task<IReadOnlyList<ManagedTwitchChannel>> GetManagedAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
        var rows = await db.TwitchChannels.Where(c => c.IsEnabled).OrderBy(c => c.Login).ToListAsync(ct);

        var unresolved = rows.Where(r => string.IsNullOrEmpty(r.UserId)).Select(r => r.Login).ToList();
        if (unresolved.Count > 0)
        {
            var token = await _tokens.GetAsync(ct);
            if (token is null)
            {
                _log.LogWarning("{Count} channel(s) need their Twitch user id resolved but no token is available (connect the bot or configure the app)", unresolved.Count);
            }
            else
            {
                try
                {
                    var users = await _helix.GetUsersByLoginAsync(token, unresolved, ct);
                    var byLogin = users.ToDictionary(u => u.Login, StringComparer.OrdinalIgnoreCase);
                    foreach (var row in rows.Where(r => string.IsNullOrEmpty(r.UserId)))
                    {
                        if (byLogin.TryGetValue(row.Login, out var u))
                        {
                            row.UserId = u.Id;
                            row.DisplayName ??= u.DisplayName;
                            row.LastResolvedAt = DateTime.UtcNow;
                            row.UpdatedAt = DateTime.UtcNow;
                        }
                        else
                        {
                            _log.LogWarning("Twitch login '{Login}' does not resolve to a user - skipped until it does (renamed? banned?)", row.Login);
                        }
                    }
                    await db.SaveChangesAsync(ct);
                }
                catch (TwitchUnauthorizedException)
                {
                    await _tokens.MarkUnauthorizedAsync(ct);
                }
            }
        }

        return rows
            .Where(r => !string.IsNullOrEmpty(r.UserId))
            .Select(r => new ManagedTwitchChannel(r.Login.ToLowerInvariant(), r.UserId!, string.IsNullOrWhiteSpace(r.DisplayName) ? r.Login : r.DisplayName!, r.CaptureChat, r.CaptureAudio))
            .ToList();
    }
}
