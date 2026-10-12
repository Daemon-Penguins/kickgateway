namespace TailoredApps.KickGateway.Mcp.OAuth;

/// <summary>
/// Configuration section <c>Mcp</c> — the HTTP transport (the <c>mcp</c> container).
/// Env: <c>Mcp__Transport</c>, <c>Mcp__ClientApiKey</c>, <c>Mcp__PublicBaseUrl</c>, <c>Mcp__OAuth__*</c>.
/// </summary>
public sealed class McpHttpOptions
{
    public const string SectionName = "Mcp";
    public const int MinClientKeyLength = 24;

    /// <summary><c>http</c> = Streamable HTTP at <c>/mcp</c>; anything else = stdio.</summary>
    public string? Transport { get; set; }

    /// <summary>
    /// Key MCP clients present as <c>Authorization: Bearer</c> / <c>X-Api-Key</c>, and the credential the OAuth
    /// consent page asks for. Defaults to <c>KickGateway:ApiKey</c> (the analytics key).
    /// </summary>
    public string? ClientApiKey { get; set; }

    /// <summary>
    /// Public origin of this server behind the reverse proxy, e.g. <c>https://gateway.example.com</c> (no path).
    /// OAuth issuer, resource and endpoint URLs are built from it. When unset they are derived from the request
    /// (<c>X-Forwarded-Proto</c>/<c>X-Forwarded-Host</c>, then scheme + host) — set it in production.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public McpOAuthOptions OAuth { get; set; } = new();
}

/// <summary>
/// The embedded OAuth 2.1 authorization server (section <c>Mcp:OAuth</c>). It exists so that MCP clients which
/// only speak OAuth (ChatGPT, Claude.ai, the MCP Inspector…) can connect; the "login" on the consent page is the
/// same analytics API key. Everything it issues (client ids, codes, access + refresh tokens) is a self-contained
/// HMAC-signed token, so the server stays stateless and needs no database.
/// </summary>
public sealed class McpOAuthOptions
{
    /// <summary>Off = API key only (no metadata endpoints, no consent page).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Secret the tokens are signed with (via HKDF). Default: derived from the client API key, so rotating the key
    /// invalidates every token at once and no extra secret is needed. Set it to rotate tokens independently.
    /// </summary>
    public string? SigningKey { get; set; }

    public int AccessTokenMinutes { get; set; } = 60;

    /// <summary>Absolute lifetime of a grant: refresh tokens are rotated but never extended past it.</summary>
    public int RefreshTokenDays { get; set; } = 90;

    public int AuthorizationCodeSeconds { get; set; } = 300;

    /// <summary>Accept <c>https://…</c> client ids that point at a Client ID Metadata Document (what ChatGPT prefers).</summary>
    public bool AllowClientMetadataDocuments { get; set; } = true;

    /// <summary>Serve RFC 7591 dynamic client registration at <c>/mcp/oauth/register</c> (Claude.ai, Claude Code, Inspector).</summary>
    public bool AllowDynamicRegistration { get; set; } = true;
}
