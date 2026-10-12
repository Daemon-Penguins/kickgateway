using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TailoredApps.KickGateway.Mcp.OAuth;

/// <summary>
/// A third-party compact JWS (a <c>private_key_jwt</c> client assertion) split into its parts. Parsing proves
/// nothing — verification against the client's JWKS is <see cref="OAuthServer"/>'s job.
/// </summary>
internal sealed record ParsedJwt(JsonObject Header, JsonObject Payload, byte[] Signature, byte[] SigningInput)
{
    private const int MaxLength = 16 * 1024;

    public string? Alg => OAuthClient.AsString(Header["alg"]);
    public string? Kid => OAuthClient.AsString(Header["kid"]);

    public static ParsedJwt? TryParse(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxLength) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            if (JsonNode.Parse(Base64Url.DecodeFromChars(parts[0])) is not JsonObject header
                || JsonNode.Parse(Base64Url.DecodeFromChars(parts[1])) is not JsonObject payload) return null;
            return new ParsedJwt(header, payload, Base64Url.DecodeFromChars(parts[2]), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }
}
