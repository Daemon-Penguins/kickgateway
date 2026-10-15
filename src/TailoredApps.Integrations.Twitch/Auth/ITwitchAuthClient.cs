namespace TailoredApps.Integrations.Twitch.Auth;

/// <summary>Result of a token exchange / refresh. <see cref="Scopes"/> is what Twitch granted.</summary>
public sealed record TwitchTokenResult(bool Success, string? AccessToken, string? RefreshToken, int ExpiresInSec, string[] Scopes, string? Error, int StatusCode);

/// <summary><c>GET /oauth2/validate</c>: who the token belongs to and what it may do.</summary>
public sealed record TwitchTokenValidation(string ClientId, string Login, string UserId, string[] Scopes, int ExpiresInSec);

/// <summary>
/// Twitch OAuth for the gateway's single developer application: the authorization-code grant for the
/// bot account, refreshes, hourly validation (Twitch requires it) and the client-credentials app token
/// used where no user is needed (Helix lookups from the capture service).
/// </summary>
public interface ITwitchAuthClient
{
    /// <summary>Authorize URL for the bot account (<c>response_type=code</c>, <c>force_verify</c> so the account picker always shows).</summary>
    string BuildAuthorizationUrl(string redirectUri, string scopes, string state, bool forceVerify = true);

    Task<TwitchTokenResult> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default);

    /// <summary>Refreshes a user token. Twitch may rotate the refresh token - always store the returned one.</summary>
    Task<TwitchTokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Null when the token is invalid or revoked (HTTP 401).</summary>
    Task<TwitchTokenValidation?> ValidateAsync(string accessToken, CancellationToken ct = default);

    /// <summary>Client-credentials app access token, cached in memory until shortly before it expires. Null when the app is not configured or Twitch refused.</summary>
    Task<string?> GetAppAccessTokenAsync(CancellationToken ct = default);
}
