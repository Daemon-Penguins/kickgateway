using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.Integrations.Kick;

/// <summary>
/// Low-level Kick REST wrapper. Multi-client aware — every call that hits the
/// OAuth token endpoint takes the clientId/secret explicitly so a single
/// gateway can host many Kick developer apps. Token storage and refresh
/// scheduling are the caller's responsibility.
/// </summary>
public interface IKickClient
{
    Task<KickTokenResult> ExchangeAuthorizationCodeAsync(string clientId, string clientSecret, string code, string codeVerifier, string redirectUri, CancellationToken ct = default);

    Task<KickTokenResult> RefreshAccessTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct = default);

    Task<KickPublicKey> GetPublicKeyAsync(CancellationToken ct = default);

    Task<KickUserInfo?> GetCurrentUserAsync(string accessToken, CancellationToken ct = default);

    Task<IReadOnlyList<KickSubscriptionInfo>> ListSubscriptionsAsync(string accessToken, CancellationToken ct = default);

    Task<KickSubscriptionInfo?> CreateSubscriptionAsync(string accessToken, string eventName, int version, string method, string? broadcasterUserId, CancellationToken ct = default);

    Task<bool> DeleteSubscriptionAsync(string accessToken, string subscriptionId, CancellationToken ct = default);
}
