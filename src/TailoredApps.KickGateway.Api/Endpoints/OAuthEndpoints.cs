using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Services;

namespace TailoredApps.KickGateway.Api.Endpoints;

public static class OAuthEndpoints
{
    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Begin the OAuth flow for a specific client app. Caller passes ?clientAppId=<guid>.
        // We generate PKCE, store it keyed by State, then 302 to Kick's authorize endpoint.
        routes.MapGet("/api/auth/kick/start", async (
            Guid clientAppId,
            string? channelSlug,
            System.Security.Claims.ClaimsPrincipal user,
            KickGatewayDbContext db,
            PkceStateStore pkce,
            IOptions<KickGlobalDefaults> defaults,
            CancellationToken ct) =>
        {
            if (!user.HasClientRoleAtLeast(clientAppId, Data.AdminRole.ClientAdmin))
                return Results.Forbid();

            var app = await db.ClientApps.FirstOrDefaultAsync(x => x.Id == clientAppId && x.IsEnabled, ct);
            if (app is null) return Results.NotFound(new { error = "client app not found / disabled" });

            var challenge = PkceHelper.Create(flow: channelSlug is null ? "broadcaster" : $"broadcaster:{channelSlug}");
            await pkce.SaveAsync(challenge, app.Id, ct);

            var url = PkceHelper.BuildAuthorizationUrl(
                authBaseUrl: defaults.Value.AuthBaseUrl,
                clientId: app.ClientId,
                redirectUri: app.RedirectUri,
                scopes: app.Scopes,
                pkce: challenge);

            return Results.Redirect(url);
        });

        routes.MapGet("/api/auth/kick/callback", async (
            string? code,
            string? state,
            string? error,
            string? error_description,
            KickGatewayDbContext db,
            PkceStateStore pkce,
            IKickClient kick,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("KickOAuthCallback");

            if (!string.IsNullOrEmpty(error))
                return Results.BadRequest(new { error, error_description });

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.BadRequest(new { error = "missing code or state" });

            var match = await pkce.TakeAsync(state, ct);
            if (match is null) return Results.BadRequest(new { error = "invalid or expired state" });
            var (entry, app) = match.Value;

            var tokens = await kick.ExchangeAuthorizationCodeAsync(
                app.ClientId, app.ClientSecret, code!, entry.CodeVerifier, app.RedirectUri, ct);
            if (!tokens.Success || tokens.AccessToken is null || tokens.RefreshToken is null)
            {
                log.LogError("Token exchange failed: {Err}", tokens.Error);
                return Results.BadRequest(new { error = "token exchange failed", detail = tokens.Error });
            }

            var user = await kick.GetCurrentUserAsync(tokens.AccessToken, ct);
            if (user is null) return Results.BadRequest(new { error = "failed to fetch user info" });

            var existing = await db.Broadcasters.FirstOrDefaultAsync(
                x => x.KickClientAppId == app.Id && x.KickUserId == user.UserId, ct);

            if (existing is null)
            {
                existing = new KickBroadcasterAccount
                {
                    KickClientAppId = app.Id,
                    KickUserId = user.UserId,
                    Username = user.Username,
                    ChannelSlug = user.Username.ToLowerInvariant(),
                };
                db.Broadcasters.Add(existing);
            }

            existing.AccessToken = tokens.AccessToken;
            existing.RefreshToken = tokens.RefreshToken;
            existing.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresInSec - 30));
            existing.Scopes = tokens.Scope ?? app.Scopes;
            existing.IsEnabled = true;
            existing.LastRefreshedAt = DateTime.UtcNow;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.Username = user.Username;
            if (string.IsNullOrEmpty(existing.ChannelSlug))
                existing.ChannelSlug = user.Username.ToLowerInvariant();

            await db.SaveChangesAsync(ct);

            // No per-broadcaster detail page yet — drop the user back on the list,
            // where the freshly connected row appears at the top.
            return Results.Redirect("/admin/broadcasters");
        });

        return routes;
    }
}
