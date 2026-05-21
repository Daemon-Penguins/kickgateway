namespace TailoredApps.Integrations.Kick.Models;

public record KickTokenResult(
    bool Success,
    string? AccessToken,
    string? RefreshToken,
    int ExpiresInSec,
    string? Scope,
    string? TokenType,
    string? Error);

public record KickPublicKey(string Pem, DateTime FetchedAt);

public record KickUserInfo(string UserId, string Username, string? Email);

public record KickSubscriptionInfo(string Id, string Event, string Version, string Method, DateTime CreatedAt);

public record PkceChallenge(string State, string CodeVerifier, string CodeChallenge, DateTime ExpiresAt, string Flow);
