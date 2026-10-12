using System.Text.Json.Nodes;

namespace TailoredApps.KickGateway.Mcp.OAuth;

/// <summary>
/// An OAuth client as the authorization server sees it — either dynamically registered (the registration is encoded
/// in the signed client id, nothing is stored) or described by a Client ID Metadata Document (the client id is the
/// https URL of that document). <paramref name="TokenEndpointAuthMethods"/> lists how it may authenticate at the token
/// endpoint; <paramref name="JwksUri"/> is where its <c>private_key_jwt</c> signing keys live.
/// </summary>
public sealed record OAuthClient(
    string Id,
    string Name,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> TokenEndpointAuthMethods,
    string? JwksUri,
    bool IsMetadataDocument)
{
    public const string AuthNone = "none";
    public const string AuthSecretPost = "client_secret_post";
    public const string AuthSecretBasic = "client_secret_basic";
    public const string AuthPrivateKeyJwt = "private_key_jwt";
    public const int MaxRedirectUris = 10;
    public const int MaxNameLength = 100;
    private const int MaxUriLength = 512;

    public bool Supports(string method) => TokenEndpointAuthMethods.Contains(method, StringComparer.Ordinal);

    /// <summary>A public client: PKCE is its only proof, no secret or assertion expected.</summary>
    public bool IsPublic => Supports(AuthNone);

    public bool UsesSecret => Supports(AuthSecretPost) || Supports(AuthSecretBasic);

    public bool AllowsRedirect(string? uri) => uri is not null && RedirectUris.Contains(uri, StringComparer.Ordinal);

    /// <summary>Hosts shown on the consent page — the spec wants the redirect host visible to the user.</summary>
    public string RedirectHosts => string.Join(", ", RedirectUris.Select(u => new Uri(u).Host).Distinct(StringComparer.OrdinalIgnoreCase));

    public bool HasLoopbackRedirect => RedirectUris.Any(u => new Uri(u).IsLoopback);

    /// <summary>What dynamic registration accepts (a registered client gets exactly one method).</summary>
    public static bool IsSupportedRegistrationAuthMethod(string? method) => method is AuthNone or AuthSecretPost or AuthSecretBasic;

    /// <summary>OAuth 2.1: redirect URIs are https, or http on the loopback interface; never with a fragment.</summary>
    public static bool IsValidRedirectUri(string? value) =>
        value is { Length: > 0 and <= MaxUriLength }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>
    /// A URL this server may fetch on a client's behalf (its metadata document, its JWKS): absolute https with a path,
    /// a DNS host name (no IP literals, nothing local — this is also the SSRF guard), no fragment, no credentials.
    /// </summary>
    public static bool IsFetchableHttpsUrl(string? value, out Uri uri)
    {
        uri = null!;
        if (value is not { Length: > 0 and <= MaxUriLength } || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (parsed.AbsolutePath.Length <= 1 || !string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        if (parsed.HostNameType != UriHostNameType.Dns || parsed.IsLoopback) return false;
        var host = parsed.Host;
        if (!host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>A Client ID Metadata Document id — the same shape as any URL we fetch.</summary>
    public static bool IsMetadataDocumentUrl(string? clientId, out Uri uri) => IsFetchableHttpsUrl(clientId, out uri);

    /// <summary>
    /// Builds the client from a fetched metadata document. Accepted token-endpoint methods: <c>none</c> (PKCE only)
    /// and <c>private_key_jwt</c> with a fetchable <c>jwks_uri</c> — ChatGPT publishes both, preferring the latter.
    /// </summary>
    public static OAuthClient? FromMetadataDocument(string url, JsonObject? document, out string problem)
    {
        problem = "";
        if (document is null) { problem = "the document is not a JSON object"; return null; }
        if (AsString(document["client_id"]) != url) { problem = "client_id in the document does not match its URL"; return null; }

        if (document["redirect_uris"] is not JsonArray uris || uris.Count is 0 or > MaxRedirectUris)
        {
            problem = $"redirect_uris must list 1-{MaxRedirectUris} URIs";
            return null;
        }
        var redirectUris = new List<string>(uris.Count);
        foreach (var node in uris)
        {
            var uri = AsString(node);
            if (!IsValidRedirectUri(uri)) { problem = "redirect_uris contains an unacceptable URI"; return null; }
            redirectUris.Add(uri!);
        }

        var declared = document["token_endpoint_auth_methods_supported"] is JsonArray methods
            ? methods.Select(AsString).Where(m => m is not null).Select(m => m!).ToList()
            : [AsString(document["token_endpoint_auth_method"]) ?? AuthNone];
        var jwksUri = AsString(document["jwks_uri"]);
        if (jwksUri is not null && !IsFetchableHttpsUrl(jwksUri, out _)) { problem = "jwks_uri is not a fetchable https URL"; return null; }

        var supported = new List<string>(2);
        if (declared.Contains(AuthNone)) supported.Add(AuthNone);
        if (declared.Contains(AuthPrivateKeyJwt) && jwksUri is not null) supported.Add(AuthPrivateKeyJwt);
        if (supported.Count == 0)
        {
            problem = declared.Contains(AuthPrivateKeyJwt)
                ? "private_key_jwt needs a jwks_uri"
                : "the client supports neither 'none' nor 'private_key_jwt' at the token endpoint";
            return null;
        }

        var name = AsString(document["client_name"]) is { Length: > 0 } n ? n.Trim() : new Uri(url).Host;
        return new OAuthClient(url, Truncate(name, MaxNameLength), redirectUris, supported, supported.Contains(AuthPrivateKeyJwt) ? jwksUri : null, IsMetadataDocument: true);
    }

    internal static string? AsString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
