using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TailoredApps.KickGateway.Mcp.OAuth;

public sealed record OAuthError(string Error, string Description, int Status = 400)
{
    public JsonObject ToJson() => new() { ["error"] = Error, ["error_description"] = Description };
}

public sealed record OAuthResult(JsonObject? Value, OAuthError? Error)
{
    public static OAuthResult Ok(JsonObject value) => new(value, null);
    public static OAuthResult Fail(string error, string description, int status = 400) => new(null, new OAuthError(error, description, status));
}

public abstract record AuthorizeOutcome
{
    /// <summary>The client or its redirect URI cannot be trusted — show the error to the user, never redirect.</summary>
    public sealed record Rejected(OAuthError Error) : AuthorizeOutcome;

    /// <summary>Known client + registered redirect URI, but a bad request — reported through the redirect.</summary>
    public sealed record RedirectWithError(string RedirectUri, OAuthError Error, string? State) : AuthorizeOutcome;

    public sealed record Valid(OAuthClient Client, string RedirectUri, string? State, string CodeChallenge) : AuthorizeOutcome;
}

public sealed record AuthorizeRequest(
    string? ClientId, string? RedirectUri, string? ResponseType, string? State, string? Scope,
    string? CodeChallenge, string? CodeChallengeMethod, string? Resource);

public sealed record TokenRequest(
    string? GrantType, string? ClientId, string? ClientSecret, string? Code, string? CodeVerifier,
    string? RedirectUri, string? RefreshToken, string? Resource,
    string? ClientAssertionType = null, string? ClientAssertion = null);

/// <summary>The URLs of this deployment, built from the public origin (configured, or derived per request).</summary>
public sealed record OAuthUrls(string Issuer)
{
    public string Resource => Issuer + "/mcp";
    public string AuthorizationEndpoint => Issuer + "/mcp/oauth/authorize";
    public string TokenEndpoint => Issuer + "/mcp/oauth/token";
    public string RegistrationEndpoint => Issuer + "/mcp/oauth/register";
    public string ResourceMetadata => Issuer + "/.well-known/oauth-protected-resource";

    private static readonly Regex SafeHost = new(@"^[A-Za-z0-9.\-\[\]:]{1,255}$", RegexOptions.Compiled);

    public static OAuthUrls For(HttpRequest request, string? configuredBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl)) return new(configuredBaseUrl.Trim().TrimEnd('/'));

        // Behind Traefik the request arrives as plain http on the container's port; the forwarded headers carry the
        // public origin. Only used to build URLs, so trusting them costs nothing — still, keep header junk out.
        var proto = First(request.Headers["X-Forwarded-Proto"]);
        if (proto is not ("https" or "http")) proto = request.Scheme;
        var host = First(request.Headers["X-Forwarded-Host"]);
        if (host is null || !SafeHost.IsMatch(host)) host = request.Host.Value ?? "localhost";
        return new($"{proto}://{host}");

        static string? First(string? values) => values?.Split(',')[0].Trim() is { Length: > 0 } v ? v : null;
    }
}

/// <summary>
/// The embedded OAuth 2.1 authorization server + the resource-server side of token validation, in one place because
/// they share the signing key. Stateless by design: clients, codes and tokens are HMAC-signed claims (see
/// <see cref="JwsSigner"/>); the only in-memory state is a best-effort "already used" set for codes, rotated refresh
/// tokens and client-assertion ids. Revoking everything = rotate the API key (the default signing secret derives from it).
/// </summary>
public sealed class OAuthServer
{
    public const string Scope = "analytics";
    public const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    private const string Subject = "analytics-key";
    private const string KeyInfo = "kickgateway-mcp-oauth-v1";
    private const int UsedPruneAt = 10_000;
    private const int UsedCap = 200_000;
    private const int MinRsaBits = 2048;
    private static readonly string[] AssertionAlgs = ["RS256", "PS256", "ES256"];
    private static readonly Regex PkceToken = new(@"^[A-Za-z0-9\-._~]{43,128}$", RegexOptions.Compiled);

    private readonly McpOAuthOptions _o;
    private readonly string _clientApiKey;
    private readonly byte[] _key;
    private readonly JwsSigner _jws;
    private readonly IClientMetadataDocuments _documents;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, long> _used = new(StringComparer.Ordinal);

    public OAuthServer(McpHttpOptions options, string clientApiKey, IClientMetadataDocuments documents, TimeProvider time, ILogger<OAuthServer> log)
    {
        _o = options.OAuth;
        _clientApiKey = clientApiKey.Trim();
        _documents = documents;
        _time = time;
        _log = log;
        Active = _o.Enabled && _clientApiKey.Length >= McpHttpOptions.MinClientKeyLength;

        var secret = string.IsNullOrWhiteSpace(_o.SigningKey) ? _clientApiKey : _o.SigningKey.Trim();
        if (secret.Length == 0) secret = "inactive";
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, info: Encoding.UTF8.GetBytes(KeyInfo));
        _jws = new JwsSigner(_key);
    }

    /// <summary>False when OAuth is disabled or no usable client key is configured — then only the API key works.</summary>
    public bool Active { get; }

    public bool DynamicRegistrationEnabled => _o.AllowDynamicRegistration;

    // ── Metadata ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>RFC 9728 — the MCP server as a protected resource.</summary>
    public JsonObject ProtectedResourceMetadata(OAuthUrls u) => new()
    {
        ["resource"] = u.Resource,
        ["authorization_servers"] = new JsonArray(u.Issuer),
        ["scopes_supported"] = new JsonArray(Scope),
        ["bearer_methods_supported"] = new JsonArray("header"),
        ["resource_name"] = "Kick chat analytics (MCP)",
    };

    /// <summary>RFC 8414 — the authorization server (same origin, endpoints under /mcp/oauth).</summary>
    public JsonObject AuthorizationServerMetadata(OAuthUrls u)
    {
        var m = new JsonObject
        {
            ["issuer"] = u.Issuer,
            ["authorization_endpoint"] = u.AuthorizationEndpoint,
            ["token_endpoint"] = u.TokenEndpoint,
            ["response_types_supported"] = new JsonArray("code"),
            ["response_modes_supported"] = new JsonArray("query"),
            ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
            ["code_challenge_methods_supported"] = new JsonArray("S256"),
            ["token_endpoint_auth_methods_supported"] = new JsonArray(OAuthClient.AuthNone, OAuthClient.AuthSecretPost, OAuthClient.AuthSecretBasic, OAuthClient.AuthPrivateKeyJwt),
            ["token_endpoint_auth_signing_alg_values_supported"] = new JsonArray(AssertionAlgs.Select(a => (JsonNode)a).ToArray()),
            ["scopes_supported"] = new JsonArray(Scope),
            ["client_id_metadata_document_supported"] = _o.AllowClientMetadataDocuments,
            ["authorization_response_iss_parameter_supported"] = true,
        };
        if (_o.AllowDynamicRegistration) m["registration_endpoint"] = u.RegistrationEndpoint;
        return m;
    }

    // ── Clients ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>RFC 7591 registration. Open to anyone (that is the point of DCR); the registration lives in the signed client id.</summary>
    public OAuthResult Register(JsonObject? request)
    {
        if (request is null) return OAuthResult.Fail("invalid_client_metadata", "a JSON object is required");

        if (request["redirect_uris"] is not JsonArray uris || uris.Count is 0 or > OAuthClient.MaxRedirectUris)
            return OAuthResult.Fail("invalid_redirect_uri", $"redirect_uris must list 1-{OAuthClient.MaxRedirectUris} absolute https URIs (http only on localhost)");
        var redirectUris = new List<string>(uris.Count);
        foreach (var node in uris)
        {
            var uri = OAuthClient.AsString(node);
            if (!OAuthClient.IsValidRedirectUri(uri)) return OAuthResult.Fail("invalid_redirect_uri", "redirect_uris must be absolute https URIs without a fragment (http only on localhost)");
            redirectUris.Add(uri!);
        }

        var method = OAuthClient.AsString(request["token_endpoint_auth_method"]) ?? OAuthClient.AuthSecretBasic; // RFC 7591 default
        if (!OAuthClient.IsSupportedRegistrationAuthMethod(method))
            return OAuthResult.Fail("invalid_client_metadata", "token_endpoint_auth_method must be none, client_secret_post or client_secret_basic (private_key_jwt needs a client id metadata document)");

        var name = OAuthClient.AsString(request["client_name"]) is { Length: > 0 } n ? n.Trim() : new Uri(redirectUris[0]).Host;
        name = OAuthClient.Truncate(name, OAuthClient.MaxNameLength);

        var issuedAt = Now();
        // Without a nonce two identical registrations in the same second would share an id (and so a secret).
        var clientId = _jws.Sign(new JsonObject
        {
            ["use"] = "client",
            ["rid"] = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(8)),
            ["name"] = name,
            ["ruris"] = new JsonArray(redirectUris.Select(r => (JsonNode)r).ToArray()),
            ["tam"] = method,
            ["iat"] = issuedAt,
        });

        var response = new JsonObject
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = issuedAt,
            ["client_name"] = name,
            ["redirect_uris"] = new JsonArray(redirectUris.Select(r => (JsonNode)r).ToArray()),
            ["token_endpoint_auth_method"] = method,
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["scope"] = Scope,
        };
        if (method != OAuthClient.AuthNone)
        {
            response["client_secret"] = ClientSecretFor(clientId);
            response["client_secret_expires_at"] = 0L;
        }
        _log.LogInformation("Registered OAuth client {Name} for {Redirects}", name, string.Join(" ", redirectUris));
        return OAuthResult.Ok(response);
    }

    public async Task<OAuthClient?> ResolveClientAsync(string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 4096) return null;

        if (OAuthClient.IsMetadataDocumentUrl(clientId, out _))
            return _o.AllowClientMetadataDocuments ? await _documents.GetAsync(clientId, ct) : null;

        if (!_o.AllowDynamicRegistration) return null;
        var p = _jws.Verify(clientId);
        if (p is null || OAuthClient.AsString(p["use"]) != "client" || p["ruris"] is not JsonArray uris) return null;
        var redirectUris = uris.Select(OAuthClient.AsString).Where(s => s is not null).Select(s => s!).ToArray();
        if (redirectUris.Length == 0) return null;
        return new OAuthClient(
            clientId,
            OAuthClient.AsString(p["name"]) ?? "client",
            redirectUris,
            [OAuthClient.AsString(p["tam"]) ?? OAuthClient.AuthSecretBasic],
            JwksUri: null,
            IsMetadataDocument: false);
    }

    // ── Authorization endpoint ────────────────────────────────────────────────────────────────────────────────

    public async Task<AuthorizeOutcome> ValidateAuthorizeAsync(AuthorizeRequest r, OAuthUrls u, CancellationToken ct)
    {
        var outcome = await ValidateAuthorizeCoreAsync(r, u, ct);
        switch (outcome)
        {
            case AuthorizeOutcome.Rejected x:
                _log.LogWarning("Authorization request refused for client {Client}: {Error} ({Description})", Describe(r.ClientId), x.Error.Error, x.Error.Description);
                break;
            case AuthorizeOutcome.RedirectWithError x:
                _log.LogInformation("Authorization request from client {Client} sent back with {Error} ({Description})", Describe(r.ClientId), x.Error.Error, x.Error.Description);
                break;
        }
        return outcome;
    }

    private async Task<AuthorizeOutcome> ValidateAuthorizeCoreAsync(AuthorizeRequest r, OAuthUrls u, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.ClientId))
            return new AuthorizeOutcome.Rejected(new("invalid_request", "client_id is required"));
        var client = await ResolveClientAsync(r.ClientId, ct);
        if (client is null)
            return new AuthorizeOutcome.Rejected(new("invalid_client", "unknown client_id (register first, or use a client id metadata document URL)"));

        var redirectUri = r.RedirectUri;
        if (string.IsNullOrEmpty(redirectUri))
        {
            if (client.RedirectUris.Count != 1)
                return new AuthorizeOutcome.Rejected(new("invalid_request", "redirect_uri is required"));
            redirectUri = client.RedirectUris[0];
        }
        if (!client.AllowsRedirect(redirectUri))
            return new AuthorizeOutcome.Rejected(new("invalid_request", "redirect_uri is not registered for this client"));

        // From here on the client is known and the redirect URI is trusted, so errors go back through it.
        if (r.ResponseType != "code")
            return new AuthorizeOutcome.RedirectWithError(redirectUri, new("unsupported_response_type", "response_type must be 'code'"), r.State);
        if (string.IsNullOrEmpty(r.CodeChallenge) || !PkceToken.IsMatch(r.CodeChallenge))
            return new AuthorizeOutcome.RedirectWithError(redirectUri, new("invalid_request", "code_challenge is required (PKCE, 43-128 characters)"), r.State);
        if (r.CodeChallengeMethod != "S256")
            return new AuthorizeOutcome.RedirectWithError(redirectUri, new("invalid_request", "code_challenge_method must be S256"), r.State);
        if (r.Resource is not null && !ResourceMatches(r.Resource, u))
            return new AuthorizeOutcome.RedirectWithError(redirectUri, new("invalid_target", $"resource must be {u.Resource}"), r.State);
        // scope: there is exactly one, and every token gets it — an unknown or missing scope is not an error.

        return new AuthorizeOutcome.Valid(client, redirectUri, r.State, r.CodeChallenge);
    }

    /// <summary>The "login": the consent page asks for the analytics API key.</summary>
    public bool CheckUserCredential(string? presented)
    {
        if (!Active || string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented.Trim())),
            SHA256.HashData(Encoding.UTF8.GetBytes(_clientApiKey)));
    }

    public string IssueCode(AuthorizeOutcome.Valid v, OAuthUrls u)
    {
        var now = Now();
        return _jws.Sign(new JsonObject
        {
            ["use"] = "code",
            ["jti"] = NewId(),
            ["iss"] = u.Issuer,
            ["aud"] = u.Resource,
            ["cid"] = ClientHash(v.Client.Id),
            ["ruri"] = v.RedirectUri,
            ["cc"] = v.CodeChallenge,
            ["iat"] = now,
            ["exp"] = now + Math.Max(30, _o.AuthorizationCodeSeconds),
        });
    }

    // ── Token endpoint ────────────────────────────────────────────────────────────────────────────────────────

    public async Task<OAuthResult> TokenAsync(TokenRequest r, OAuthUrls u, CancellationToken ct)
    {
        var result = await TokenCoreAsync(r, u, ct);
        var clientId = r.ClientId ?? (ParsedJwt.TryParse(r.ClientAssertion) is { } jwt ? OAuthClient.AsString(jwt.Payload["iss"]) : null);
        if (result.Error is { } e)
            _log.LogWarning("Token request refused: {Error} ({Description}) - grant {Grant}, client {Client}, auth {Auth}",
                e.Error, e.Description, r.GrantType ?? "-", Describe(clientId), AuthStyle(r));
        else
            _log.LogInformation("Tokens issued: grant {Grant}, client {Client}, auth {Auth}", r.GrantType, Describe(clientId), AuthStyle(r));
        return result;
    }

    private async Task<OAuthResult> TokenCoreAsync(TokenRequest r, OAuthUrls u, CancellationToken ct)
    {
        var clientId = r.ClientId;
        ParsedJwt? assertion = null;
        if (!string.IsNullOrEmpty(r.ClientAssertion) || !string.IsNullOrEmpty(r.ClientAssertionType))
        {
            if (r.ClientAssertionType != JwtBearerAssertionType)
                return OAuthResult.Fail("invalid_client", "client_assertion_type must be " + JwtBearerAssertionType, 401);
            assertion = ParsedJwt.TryParse(r.ClientAssertion);
            if (assertion is null) return OAuthResult.Fail("invalid_client", "client_assertion is not a JWT", 401);
            var issuer = OAuthClient.AsString(assertion.Payload["iss"]);
            if (string.IsNullOrEmpty(issuer)) return OAuthResult.Fail("invalid_client", "client_assertion has no iss", 401);
            if (clientId is not null && clientId != issuer)
                return OAuthResult.Fail("invalid_client", "client_id does not match the client_assertion issuer", 401);
            clientId ??= issuer; // RFC 7523: client_id is optional when the assertion identifies the client
        }

        var client = await ResolveClientAsync(clientId, ct);
        if (client is null) return OAuthResult.Fail("invalid_client", "unknown client_id", 401);

        if (assertion is not null)
        {
            var problem = await VerifyAssertionAsync(client, assertion, u, ct);
            if (problem is not null) return OAuthResult.Fail("invalid_client", "client_assertion rejected: " + problem, 401);
        }
        else if (!client.IsPublic)
        {
            if (!client.UsesSecret || !FixedEquals(r.ClientSecret ?? "", ClientSecretFor(client.Id)))
                return OAuthResult.Fail("invalid_client", "client authentication failed", 401);
        }

        if (r.Resource is not null && !ResourceMatches(r.Resource, u))
            return OAuthResult.Fail("invalid_target", $"resource must be {u.Resource}");

        return r.GrantType switch
        {
            "authorization_code" => ExchangeCode(r, u, client),
            "refresh_token" => Refresh(r, u, client),
            _ => OAuthResult.Fail("unsupported_grant_type", "grant_type must be authorization_code or refresh_token"),
        };
    }

    private OAuthResult ExchangeCode(TokenRequest r, OAuthUrls u, OAuthClient client)
    {
        if (string.IsNullOrEmpty(r.Code) || string.IsNullOrEmpty(r.CodeVerifier))
            return OAuthResult.Fail("invalid_request", "code and code_verifier are required");
        var p = ReadToken(r.Code, "code", u, out var problem);
        if (p is null) return OAuthResult.Fail("invalid_grant", "invalid authorization code: " + problem);
        if (OAuthClient.AsString(p["cid"]) != ClientHash(client.Id))
            return OAuthResult.Fail("invalid_grant", "the code was issued to another client");
        if (r.RedirectUri is not null && r.RedirectUri != OAuthClient.AsString(p["ruri"]))
            return OAuthResult.Fail("invalid_grant", "redirect_uri does not match the authorization request");
        if (!PkceToken.IsMatch(r.CodeVerifier) || !PkceVerifies(r.CodeVerifier, OAuthClient.AsString(p["cc"])))
            return OAuthResult.Fail("invalid_grant", "code_verifier does not match code_challenge");
        if (!MarkUsed(p)) return OAuthResult.Fail("invalid_grant", "the code has already been used");

        var grantExpires = Now() + Math.Max(1, _o.RefreshTokenDays) * 86_400L;
        return OAuthResult.Ok(IssueTokens(u, ClientHash(client.Id), grantExpires));
    }

    private OAuthResult Refresh(TokenRequest r, OAuthUrls u, OAuthClient client)
    {
        if (string.IsNullOrEmpty(r.RefreshToken)) return OAuthResult.Fail("invalid_request", "refresh_token is required");
        var p = ReadToken(r.RefreshToken, "refresh", u, out var problem);
        if (p is null) return OAuthResult.Fail("invalid_grant", "invalid refresh token: " + problem);
        if (OAuthClient.AsString(p["cid"]) != ClientHash(client.Id))
            return OAuthResult.Fail("invalid_grant", "the refresh token was issued to another client");
        if (!MarkUsed(p)) return OAuthResult.Fail("invalid_grant", "the refresh token has already been rotated");

        return OAuthResult.Ok(IssueTokens(u, ClientHash(client.Id), Long(p["exp"]) ?? Now()));
    }

    private JsonObject IssueTokens(OAuthUrls u, string clientHash, long grantExpires)
    {
        var now = Now();
        var accessExpires = Math.Min(now + Math.Max(1, _o.AccessTokenMinutes) * 60L, grantExpires);
        var access = _jws.Sign(new JsonObject
        {
            ["use"] = "access",
            ["jti"] = NewId(),
            ["iss"] = u.Issuer,
            ["aud"] = u.Resource,
            ["sub"] = Subject,
            ["client_id"] = clientHash,
            ["scope"] = Scope,
            ["iat"] = now,
            ["exp"] = accessExpires,
        });
        var refresh = _jws.Sign(new JsonObject
        {
            ["use"] = "refresh",
            ["jti"] = NewId(),
            ["iss"] = u.Issuer,
            ["aud"] = u.Resource,
            ["cid"] = clientHash,
            ["iat"] = now,
            ["exp"] = grantExpires,
        });
        return new JsonObject
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = Math.Max(0, accessExpires - now),
            ["refresh_token"] = refresh,
            ["scope"] = Scope,
        };
    }

    // ── private_key_jwt (RFC 7523) ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies a client assertion against the client's published JWKS: iss = sub = client id, aud = our token
    /// endpoint (or issuer), unexpired, a single-use jti, and a signature by one of the client's keys (RS256, PS256 or
    /// ES256). An unknown kid triggers one cache-bypassing refetch, which is how key rotation shows up.
    /// </summary>
    private async Task<string?> VerifyAssertionAsync(OAuthClient client, ParsedJwt jwt, OAuthUrls u, CancellationToken ct)
    {
        if (!client.Supports(OAuthClient.AuthPrivateKeyJwt) || client.JwksUri is null) return "the client is not registered for private_key_jwt";
        if (jwt.Alg is null || !AssertionAlgs.Contains(jwt.Alg)) return "alg must be RS256, PS256 or ES256";
        var p = jwt.Payload;
        if (OAuthClient.AsString(p["iss"]) != client.Id || OAuthClient.AsString(p["sub"]) != client.Id) return "iss and sub must equal the client_id";
        if (!AudienceContains(p["aud"], u.TokenEndpoint, u.Issuer)) return "aud must be the token endpoint or the issuer";
        if (Long(p["exp"]) is not { } exp || exp <= Now()) return "expired";
        var jti = OAuthClient.AsString(p["jti"]);
        if (string.IsNullOrEmpty(jti)) return "jti is required";

        var jwks = await _documents.GetJwksAsync(client.JwksUri, refresh: false, ct);
        if (!VerifyWithJwks(jwks, jwt))
        {
            if (jwks is not null && jwt.Kid is not null && HasKid(jwks, jwt.Kid)) return "signature does not verify against the client's JWKS";
            jwks = await _documents.GetJwksAsync(client.JwksUri, refresh: true, ct);
            if (!VerifyWithJwks(jwks, jwt)) return jwks is null ? "the client's JWKS could not be fetched" : "no key in the client's JWKS verifies the signature";
        }

        if (!MarkUsed("assertion|" + ClientHash(client.Id) + "|" + jti, exp)) return "jti has already been used";
        return null;
    }

    private static bool HasKid(JsonObject jwks, string kid) =>
        jwks["keys"] is JsonArray keys && keys.Any(k => k is JsonObject key && OAuthClient.AsString(key["kid"]) == kid);

    private static bool VerifyWithJwks(JsonObject? jwks, ParsedJwt jwt)
    {
        if (jwks?["keys"] is not JsonArray keys) return false;
        foreach (var node in keys)
        {
            if (node is not JsonObject key) continue;
            if (jwt.Kid is not null && OAuthClient.AsString(key["kid"]) != jwt.Kid) continue;
            if (OAuthClient.AsString(key["use"]) is { } use && use != "sig") continue;
            if (VerifyWithKey(key, jwt)) return true;
        }
        return false;
    }

    private static bool VerifyWithKey(JsonObject key, ParsedJwt jwt)
    {
        try
        {
            switch (jwt.Alg)
            {
                case "RS256" or "PS256" when OAuthClient.AsString(key["kty"]) == "RSA":
                {
                    using var rsa = RSA.Create(new RSAParameters
                    {
                        Modulus = Base64Url.DecodeFromChars(OAuthClient.AsString(key["n"]) ?? ""),
                        Exponent = Base64Url.DecodeFromChars(OAuthClient.AsString(key["e"]) ?? ""),
                    });
                    if (rsa.KeySize < MinRsaBits) return false;
                    var padding = jwt.Alg == "PS256" ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
                    return rsa.VerifyData(jwt.SigningInput, jwt.Signature, HashAlgorithmName.SHA256, padding);
                }
                case "ES256" when OAuthClient.AsString(key["kty"]) == "EC" && OAuthClient.AsString(key["crv"]) == "P-256":
                {
                    using var ecdsa = ECDsa.Create(new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint
                        {
                            X = Base64Url.DecodeFromChars(OAuthClient.AsString(key["x"]) ?? ""),
                            Y = Base64Url.DecodeFromChars(OAuthClient.AsString(key["y"]) ?? ""),
                        },
                    });
                    return ecdsa.VerifyData(jwt.SigningInput, jwt.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
                default:
                    return false;
            }
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool AudienceContains(JsonNode? aud, params string[] accepted) => aud switch
    {
        JsonArray many => many.Any(a => OAuthClient.AsString(a) is { } s && accepted.Contains(s, StringComparer.Ordinal)),
        _ => OAuthClient.AsString(aud) is { } one && accepted.Contains(one, StringComparer.Ordinal),
    };

    // ── Resource server ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Signature, type, expiry, issuer and audience — the token must have been minted for this very URL.</summary>
    public bool TryValidateAccessToken(string? token, OAuthUrls u, out string problem) =>
        ReadToken(token, "access", u, out problem) is not null;

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private JsonObject? ReadToken(string? token, string use, OAuthUrls u, out string problem)
    {
        problem = "";
        var p = _jws.Verify(token);
        if (p is null) { problem = "bad signature or format"; return null; }
        if (OAuthClient.AsString(p["use"]) != use) { problem = "wrong token type"; return null; }
        if (Long(p["exp"]) is not { } exp || exp <= Now()) { problem = "expired"; return null; }
        if (OAuthClient.AsString(p["iss"]) != u.Issuer) { problem = "issuer mismatch"; return null; }
        if (OAuthClient.AsString(p["aud"]) != u.Resource) { problem = "audience mismatch"; return null; }
        return p;
    }

    private bool MarkUsed(JsonObject p)
    {
        var jti = OAuthClient.AsString(p["jti"]);
        return jti is not null && MarkUsed(jti, Long(p["exp"]) ?? Now());
    }

    /// <summary>Best effort, per instance: a code, a rotated refresh token or a client assertion is accepted once.</summary>
    private bool MarkUsed(string id, long expires)
    {
        if (_used.Count > UsedPruneAt)
        {
            var now = Now();
            foreach (var kv in _used) if (kv.Value <= now) _used.TryRemove(kv.Key, out _);
            if (_used.Count > UsedCap)
            {
                _log.LogWarning("Used-token set exceeded {Cap} entries; clearing it (replay protection is best effort)", UsedCap);
                _used.Clear();
            }
        }
        return _used.TryAdd(id, expires);
    }

    private string ClientSecretFor(string clientId) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("client_secret\n" + clientId)));

    private static string ClientHash(string clientId) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)));

    /// <summary>For logs: a metadata-document id is a URL worth seeing; a registered id is a long blob, so just its hash prefix.</summary>
    private static string Describe(string? clientId) => clientId switch
    {
        null or "" => "-",
        _ when clientId.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => OAuthClient.Truncate(clientId, 120),
        _ => "registered:" + ClientHash(clientId)[..12],
    };

    private static string AuthStyle(TokenRequest r) =>
        r.ClientAssertion is not null ? OAuthClient.AuthPrivateKeyJwt
        : r.ClientSecret is not null ? "client_secret"
        : OAuthClient.AuthNone;

    private static bool PkceVerifies(string verifier, string? challenge) =>
        challenge is not null && FixedEquals(Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), challenge);

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>RFC 8707: lowercase scheme + host, no trailing slash, no fragment; the client may send any of these forms.</summary>
    internal static bool ResourceMatches(string presented, OAuthUrls u) =>
        Normalize(presented) is { } p && p == Normalize(u.Resource);

    private static string? Normalize(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeSeconds();

    private static string NewId() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private static long? Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
}
