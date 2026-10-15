using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Twitch;
using TailoredApps.Integrations.Twitch.Auth;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.TwitchGateway.Listener.Roster;

/// <summary>Who the current token belongs to (the bot) - EventSub chat conditions need the bot's user id.</summary>
public sealed record TwitchBotIdentity(Guid AccountId, string UserId, string Login);

/// <summary>
/// Hands out the bot account's user access token (refreshed ahead of expiry, validated hourly as Twitch
/// requires), falling back to the app token for Helix lookups that need no user. The bot row is managed
/// by the Api (<c>/admin/twitch</c>); this process only reads and refreshes it. Every refresh rotates the
/// refresh token, so the new one is persisted before anything else happens.
/// </summary>
public sealed class TwitchAccessTokenSource
{
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ITwitchAuthClient _auth;
    private readonly TwitchOptions _twitch;
    private readonly TwitchListenerOptions _opts;
    private readonly ILogger<TwitchAccessTokenSource> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TwitchBotIdentity? _identity;
    private string? _token;
    private DateTime _expiresAt;
    private DateTime _lastValidated;
    private DateTime _nextAttempt;
    private bool _forceRefresh;

    public TwitchAccessTokenSource(IServiceScopeFactory scopes, ITwitchAuthClient auth, IOptions<TwitchOptions> twitch,
        IOptions<TwitchListenerOptions> opts, ILogger<TwitchAccessTokenSource> log)
    {
        _scopes = scopes;
        _auth = auth;
        _twitch = twitch.Value;
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>The bot identity once a token was loaded; null until then.</summary>
    public TwitchBotIdentity? Identity => _identity;

    /// <summary>A valid bot user token, or null when no bot is connected / the app is not configured / Twitch refuses. Never throws.</summary>
    public async Task<string?> GetBotTokenAsync(CancellationToken ct)
    {
        if (!_twitch.IsConfigured) return null;
        var now = DateTime.UtcNow;
        if (!_forceRefresh && _token is not null && now < _expiresAt - RefreshAhead && now - _lastValidated < TimeSpan.FromMinutes(Math.Max(5, _opts.TokenValidateMinutes)))
            return _token;
        if (now < _nextAttempt) return _token is not null && now < _expiresAt ? _token : null;

        await _gate.WaitAsync(ct);
        try
        {
            now = DateTime.UtcNow;
            if (!_forceRefresh && _token is not null && now < _expiresAt - RefreshAhead && now - _lastValidated < TimeSpan.FromMinutes(Math.Max(5, _opts.TokenValidateMinutes)))
                return _token;
            if (now < _nextAttempt) return _token is not null && now < _expiresAt ? _token : null;

            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KickGatewayDbContext>();
            var bot = await db.TwitchBotAccounts.Where(b => b.IsEnabled).OrderBy(b => b.CreatedAt).FirstOrDefaultAsync(ct);
            if (bot is null || string.IsNullOrEmpty(bot.RefreshToken))
            {
                if (_identity is not null || _token is not null) _log.LogWarning("Twitch bot account disappeared / was disabled - EventSub will stop at the next reconcile");
                _identity = null;
                _token = null;
                _nextAttempt = now + RetryAfterFailure;
                return null;
            }

            var needRefresh = _forceRefresh || string.IsNullOrEmpty(bot.AccessToken) || bot.AccessTokenExpiresAt <= now + RefreshAhead;
            if (!needRefresh)
            {
                // Hourly validation: Twitch revokes apps that skip it; a 401 here means the user disconnected us.
                var validation = await _auth.ValidateAsync(bot.AccessToken, ct);
                if (validation is null)
                {
                    _log.LogWarning("Twitch bot token failed validation - refreshing");
                    needRefresh = true;
                }
                else
                {
                    bot.LastValidatedAt = now;
                    bot.UserId = validation.UserId.Length > 0 ? validation.UserId : bot.UserId;
                    bot.Login = validation.Login.Length > 0 ? validation.Login : bot.Login;
                    bot.LastError = null;
                }
            }

            if (needRefresh)
            {
                var result = await _auth.RefreshAsync(bot.RefreshToken, ct);
                if (!result.Success || result.AccessToken is null)
                {
                    bot.LastError = $"{now:u}: refresh failed - {result.Error}";
                    bot.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                    _log.LogError("Twitch bot token refresh failed ({Error}). Reconnect the bot at /admin/twitch if this persists", result.Error);
                    _token = null;
                    _nextAttempt = now + RetryAfterFailure;
                    _forceRefresh = false;
                    return null;
                }
                bot.AccessToken = result.AccessToken;
                if (result.RefreshToken is not null) bot.RefreshToken = result.RefreshToken;
                bot.AccessTokenExpiresAt = now.AddSeconds(Math.Max(60, result.ExpiresInSec));
                bot.Scopes = result.Scopes.Length > 0 ? string.Join(' ', result.Scopes) : bot.Scopes;
                bot.LastRefreshedAt = now;
                bot.LastValidatedAt = now;
                bot.LastError = null;
                _log.LogInformation("Twitch bot token refreshed for {Login} (expires in {Min:F0} min)", bot.Login, (bot.AccessTokenExpiresAt - now).TotalMinutes);
            }

            bot.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            _identity = new TwitchBotIdentity(bot.Id, bot.UserId, bot.Login);
            _token = bot.AccessToken;
            _expiresAt = bot.AccessTokenExpiresAt;
            _lastValidated = now;
            _forceRefresh = false;
            _nextAttempt = DateTime.MinValue;
            return _token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the Twitch bot token failed");
            _nextAttempt = DateTime.UtcNow + RetryAfterFailure;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Bot token when available, else the app token (Helix users/streams work with either).</summary>
    public async Task<string?> GetAsync(CancellationToken ct) =>
        await GetBotTokenAsync(ct) ?? await _auth.GetAppAccessTokenAsync(ct);

    /// <summary>Helix answered 401 with the current token: refresh it on the next call.</summary>
    public Task MarkUnauthorizedAsync(CancellationToken ct)
    {
        _forceRefresh = true;
        _nextAttempt = DateTime.MinValue;
        return Task.CompletedTask;
    }
}
