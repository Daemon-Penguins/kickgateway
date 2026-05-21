using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.Integrations.Kick;

public interface IKickSignatureVerifier
{
    Task<bool> VerifyAsync(string messageId, string timestamp, string body, string signatureBase64, CancellationToken ct = default);

    Task<KickPublicKey> GetCachedKeyAsync(CancellationToken ct = default, bool forceRefresh = false);
}
