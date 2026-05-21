using System.Security.Cryptography;
using System.Text;
using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.Integrations.Kick;

public static class PkceHelper
{
    public static PkceChallenge Create(string flow = "broadcaster", TimeSpan? ttl = null)
    {
        var verifier = RandomBase64Url(64);
        var state = RandomBase64Url(32);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64Url(hash);
        var expires = DateTime.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(5));
        return new PkceChallenge(state, verifier, challenge, expires, flow);
    }

    public static string BuildAuthorizationUrl(string authBaseUrl, string clientId, string redirectUri, string scopes, PkceChallenge pkce)
    {
        var qs = new[]
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(scopes)}",
            $"code_challenge={pkce.CodeChallenge}",
            "code_challenge_method=S256",
            $"state={pkce.State}"
        };
        return $"{authBaseUrl.TrimEnd('/')}/oauth/authorize?" + string.Join("&", qs);
    }

    private static string RandomBase64Url(int byteCount) => Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
