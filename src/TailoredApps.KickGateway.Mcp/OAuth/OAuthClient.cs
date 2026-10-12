using System.Text.Json.Nodes;

namespace TailoredApps.KickGateway.Mcp.OAuth;

/// <summary>
/// An OAuth client as the authorization server sees it — either dynamically registered (the registration is encoded
/// in the signed client id, nothing is stored) or described by a Client ID Metadata Document (the client id is the
/// https URL of that document).
/// </summary>
public sealed record OAuthClient(
    string Id,
    string Name,
    IReadOnlyList<string> RedirectUris,
    string TokenEndpointAuthMethod,
    bool IsMetadataDocument)
{
    public const string AuthNone = "none";
    public const string AuthSecretPost = "client_secret_post";
    public const string AuthSecretBasic = "client_secret_basic";
    public const int MaxRedirectUris = 10;
    public const int MaxNameLength = 100;
    private const int MaxUriLength = 512;

    public bool IsPublic => TokenEndpointAuthMethod == AuthNone;

    public bool AllowsRedirect(string? uri) => uri is not null && RedirectUris.Contains(uri, StringComparer.Ordinal);

    /// <summary>Hosts shown on the consent page — the spec wants the redirect host visible to the user.</summary>
    public string RedirectHosts => string.Join(", ", RedirectUris.Select(u => new Uri(u).Host).Distinct(StringComparer.OrdinalIgnoreCase));

    public bool HasLoopbackRedirect => RedirectUris.Any(u => new Uri(u).IsLoopback);

    public static bool IsSupportedAuthMethod(string? method) => method is AuthNone or AuthSecretPost or AuthSecretBasic;

    /// <summary>OAuth 2.1: redirect URIs are https, or http on the loopback interface; never with a fragment.</summary>
    public static bool IsValidRedirectUri(string? value) =>
        value is { Length: > 0 and <= MaxUriLength }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>
    /// A Client ID Metadata Document id: absolute https URL with a path, a DNS host name (no IP literals, nothing
    /// local — the server fetches it, so this is also the SSRF guard), no fragment, no credentials.
    /// </summary>
    public static bool IsMetadataDocumentUrl(string? clientId, out Uri uri)
    {
        uri = null!;
        if (clientId is not { Length: > 0 and <= MaxUriLength } || !clientId.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (parsed.AbsolutePath.Length <= 1 || !string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        if (parsed.HostNameType != UriHostNameType.Dns || parsed.IsLoopback) return false;
        var host = parsed.Host;
        if (!host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// Builds the client from a fetched metadata document. Only public clients are accepted — <c>private_key_jwt</c>
    /// (ChatGPT's preferred method, with <c>none</c> as its fallback) is not implemented; PKCE binds the code anyway.
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

        var supported = document["token_endpoint_auth_methods_supported"] is JsonArray methods
            ? methods.Select(AsString).ToList()
            : [AsString(document["token_endpoint_auth_method"]) ?? AuthNone];
        if (!supported.Contains(AuthNone))
        {
            problem = "the client does not support the 'none' token endpoint auth method (private_key_jwt is not implemented here)";
            return null;
        }

        var name = AsString(document["client_name"]) is { Length: > 0 } n ? n.Trim() : new Uri(url).Host;
        return new OAuthClient(url, Truncate(name, MaxNameLength), redirectUris, AuthNone, IsMetadataDocument: true);
    }

    internal static string? AsString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
