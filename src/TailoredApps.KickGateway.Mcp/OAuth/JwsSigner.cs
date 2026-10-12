using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TailoredApps.KickGateway.Mcp.OAuth;

/// <summary>
/// Compact JWS, HS256 only. Every token this server hands out (client ids, authorization codes, access and refresh
/// tokens) is one of these, which is what lets the authorization server stay stateless: the token carries its own
/// claims and the signature proves we issued them. Only we ever verify them, so no other algorithm is accepted.
/// </summary>
internal sealed class JwsSigner(byte[] key)
{
    private const int MaxTokenLength = 8192;
    private static readonly string Header = Base64Url.EncodeToString("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8);

    public string Sign(JsonObject payload)
    {
        var body = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signingInput = Header + "." + body;
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signingInput));
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>The payload when the signature verifies, else null. Expiry is the caller's job (it owns the clock).</summary>
    public JsonObject? Verify(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return null;
        var parts = token.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Header, StringComparison.Ordinal)) return null;

        byte[] signature;
        try { signature = Base64Url.DecodeFromChars(parts[2]); }
        catch (FormatException) { return null; }

        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]));
        if (!CryptographicOperations.FixedTimeEquals(signature, expected)) return null;

        try { return JsonNode.Parse(Base64Url.DecodeFromChars(parts[1])) as JsonObject; }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }
}
