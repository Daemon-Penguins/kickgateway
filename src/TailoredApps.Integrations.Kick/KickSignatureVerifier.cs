using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.Integrations.Kick;

public class KickSignatureVerifier : IKickSignatureVerifier
{
    private readonly IKickClient _api;
    private readonly KickGlobalDefaults _defaults;
    private readonly ILogger<KickSignatureVerifier> _log;

    private KickPublicKey? _cached;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public KickSignatureVerifier(IKickClient api, IOptions<KickGlobalDefaults> defaults, ILogger<KickSignatureVerifier> log)
    {
        _api = api;
        _defaults = defaults.Value;
        _log = log;
    }

    public async Task<KickPublicKey> GetCachedKeyAsync(CancellationToken ct = default, bool forceRefresh = false)
    {
        var ttl = TimeSpan.FromMinutes(Math.Max(1, _defaults.PublicKeyCacheMinutes));
        if (!forceRefresh && _cached is { } c && (DateTime.UtcNow - c.FetchedAt) < ttl)
            return c;

        await _lock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cached is { } c2 && (DateTime.UtcNow - c2.FetchedAt) < ttl)
                return c2;
            var fresh = await _api.GetPublicKeyAsync(ct);
            _cached = fresh;
            _log.LogInformation("Refreshed Kick public key (length={Len})", fresh.Pem.Length);
            return fresh;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> VerifyAsync(string messageId, string timestamp, string body, string signatureBase64, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(signatureBase64) || string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(timestamp))
            return false;

        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(signatureBase64); }
        catch { return false; }

        var dataBytes = Encoding.UTF8.GetBytes($"{messageId}.{timestamp}.{body}");

        // Public key rotates — retry once with a force-refresh on the first failure.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var key = await GetCachedKeyAsync(ct, forceRefresh: attempt > 0);
            using var rsa = RSA.Create();
            try { rsa.ImportFromPem(key.Pem.AsSpan()); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to import Kick public key (attempt {Attempt})", attempt + 1);
                if (attempt > 0) return false;
                continue;
            }

            try
            {
                var ok = rsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (ok) return true;
                if (attempt == 0)
                {
                    _log.LogWarning("Kick signature verify failed on cached key — refreshing and retrying");
                    continue;
                }
                return false;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "RSA.VerifyData threw");
                return false;
            }
        }
        return false;
    }
}
