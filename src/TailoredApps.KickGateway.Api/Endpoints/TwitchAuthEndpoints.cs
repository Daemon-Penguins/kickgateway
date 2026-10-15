using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Twitch;
using TailoredApps.Integrations.Twitch.Auth;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Endpoints;

/// <summary>
/// Connects the gateway's Twitch <b>bot account</b> - the user whose token the Twitch listener uses for
/// EventSub chat subscriptions (<c>user:read:chat</c> from the chatting user; no broadcaster consent needed).
/// Authorization-code grant against the single configured developer application; the round-trip state is a
/// DataProtection-sealed, time-limited token (no table). One bot row is kept; connecting again replaces it.
/// </summary>
public static class TwitchAuthEndpoints
{
    /// <summary>Path of the OAuth callback; the Twitch console must list <c>{public origin}{CallbackPath}</c> exactly.</summary>
    public const string CallbackPath = "/api/auth/twitch/callback";

    private const string StateProtectorPurpose = "twitch-bot-oauth-state";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Admin-gated start (SuperAdmin) - mapped inside the authenticated admin group.</summary>
    public static IEndpointRouteBuilder MapTwitchAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/twitch/start", (
            HttpContext http,
            System.Security.Claims.ClaimsPrincipal user,
            IOptions<TwitchOptions> twitch,
            ITwitchAuthClient auth,
            IDataProtectionProvider dp) =>
        {
            if (!user.IsSuperAdmin()) return Results.Forbid();
            if (!twitch.Value.IsConfigured)
                return Results.BadRequest(new { error = "Twitch developer application is not configured (Twitch:ClientId / Twitch:ClientSecret)" });

            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var state = dp.CreateProtector(StateProtectorPurpose).ToTimeLimitedDataProtector().Protect(nonce, StateLifetime);
            var url = auth.BuildAuthorizationUrl(RedirectUri(http), twitch.Value.BotScopes, state, forceVerify: true);
            return Results.Redirect(url);
        });

        return routes;
    }

    /// <summary>Anonymous callback - the admin cookie may be gone after Twitch's consent page; the sealed state is the proof.</summary>
    public static IEndpointRouteBuilder MapTwitchAuthCallback(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(CallbackPath, async (
            HttpContext http,
            string? code,
            string? state,
            string? error,
            string? error_description,
            KickGatewayDbContext db,
            ITwitchAuthClient auth,
            IDataProtectionProvider dp,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("TwitchOAuthCallback");
            if (!string.IsNullOrEmpty(error)) return Results.BadRequest(new { error, error_description });
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return Results.BadRequest(new { error = "missing code or state" });

            try
            {
                dp.CreateProtector(StateProtectorPurpose).ToTimeLimitedDataProtector().Unprotect(state);
            }
            catch (CryptographicException)
            {
                return Results.BadRequest(new { error = "invalid or expired state" });
            }

            var tokens = await auth.ExchangeCodeAsync(code, RedirectUri(http), ct);
            if (!tokens.Success || tokens.AccessToken is null || tokens.RefreshToken is null)
            {
                log.LogError("Twitch token exchange failed: {Error}", tokens.Error);
                return Results.BadRequest(new { error = "token exchange failed", detail = tokens.Error });
            }

            var who = await auth.ValidateAsync(tokens.AccessToken, ct);
            if (who is null || who.UserId.Length == 0) return Results.BadRequest(new { error = "could not validate the new token" });

            var now = DateTime.UtcNow;
            var bot = await db.TwitchBotAccounts.OrderBy(b => b.CreatedAt).FirstOrDefaultAsync(ct);
            if (bot is null)
            {
                bot = new TwitchBotAccount { CreatedAt = now };
                db.TwitchBotAccounts.Add(bot);
            }
            bot.UserId = who.UserId;
            bot.Login = who.Login;
            bot.DisplayName = who.Login;
            bot.AccessToken = tokens.AccessToken;
            bot.RefreshToken = tokens.RefreshToken;
            bot.Scopes = string.Join(' ', tokens.Scopes.Length > 0 ? tokens.Scopes : who.Scopes);
            bot.AccessTokenExpiresAt = now.AddSeconds(Math.Max(60, tokens.ExpiresInSec));
            bot.LastRefreshedAt = now;
            bot.LastValidatedAt = now;
            bot.LastError = null;
            bot.IsEnabled = true;
            bot.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            log.LogInformation("Twitch bot account connected: {Login} ({UserId}) with scopes {Scopes}", bot.Login, bot.UserId, bot.Scopes);
            return Results.Redirect("/admin/twitch");
        }).AllowAnonymous();

        return routes;
    }

    /// <summary>Scheme + host come from the request (X-Forwarded-Proto is honoured by the pipeline), so this is the public URL.</summary>
    private static string RedirectUri(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{CallbackPath}";
}
