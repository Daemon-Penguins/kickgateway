using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using TailoredApps.KickGateway.Mcp;
using TailoredApps.KickGateway.Mcp.OAuth;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// The MCP server's embedded OAuth 2.1 authorization server: PKCE flow, client registration (dynamic + metadata
/// documents), token binding to this resource, rotation/expiry, and the HTTP pipeline end to end (in-process Kestrel).
/// </summary>
public class McpOAuthTests
{
    private const string ApiKey = "test-analytics-key-0123456789abcdef";
    private const string Redirect = "https://client.example/cb";
    private static readonly OAuthUrls Urls = new("https://gateway.example.com");

    private const string ChatGptClientJson =
        """
        {"client_id":"https://chatgpt.com/oauth/client.json","client_uri":"https://chatgpt.com/",
         "redirect_uris":["https://chatgpt.com/connector_platform_oauth_redirect"],
         "token_endpoint_auth_method":"private_key_jwt","token_endpoint_auth_methods_supported":["none","private_key_jwt"],
         "grant_types":["authorization_code","refresh_token"],"response_types":["code"],"client_name":"ChatGPT",
         "logo_uri":"https://persistent.oaistatic.com/sonic/misc/openai-logo.png",
         "token_endpoint_auth_signing_alg":"RS256","jwks_uri":"https://chatgpt.com/oauth/jwks.json"}
        """;

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubDocuments(Func<string, OAuthClient?> resolve) : IClientMetadataDocuments
    {
        public Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct) => Task.FromResult(resolve(clientId));
    }

    private static OAuthServer NewServer(TestClock? clock = null, IClientMetadataDocuments? documents = null, Action<McpOAuthOptions>? configure = null)
    {
        var options = new McpHttpOptions();
        configure?.Invoke(options.OAuth);
        return new OAuthServer(options, ApiKey, documents ?? new StubDocuments(_ => null), clock ?? new TestClock(), NullLogger<OAuthServer>.Instance);
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string RegisterPublic(OAuthServer s, string redirect = Redirect, string name = "Test client")
    {
        var r = s.Register(new JsonObject { ["client_name"] = name, ["redirect_uris"] = new JsonArray(redirect), ["token_endpoint_auth_method"] = "none" });
        Assert.Null(r.Error);
        return r.Value!["client_id"]!.GetValue<string>();
    }

    private static AuthorizeRequest Authorize(string clientId, string challenge, string redirect = Redirect, string? resource = "https://gateway.example.com/mcp", string? method = "S256", string responseType = "code") =>
        new(clientId, redirect, responseType, "xyz", "analytics", challenge, method, resource);

    private static async Task<AuthorizeOutcome.Valid> ValidAuthorize(OAuthServer s, string clientId, string challenge, string redirect = Redirect) =>
        Assert.IsType<AuthorizeOutcome.Valid>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, redirect), Urls, default));

    private static TokenRequest CodeGrant(string clientId, string code, string verifier, string? redirect = Redirect, string? resource = "https://gateway.example.com/mcp", string? secret = null) =>
        new("authorization_code", clientId, secret, code, verifier, redirect, null, resource);

    private static TokenRequest RefreshGrant(string clientId, string refreshToken) =>
        new("refresh_token", clientId, null, null, null, null, refreshToken, null);

    [Fact]
    public async Task Registered_client_completes_pkce_flow_and_tokens_are_bound_to_this_resource()
    {
        var clock = new TestClock();
        var s = NewServer(clock);
        var clientId = RegisterPublic(s);
        var (verifier, challenge) = Pkce();

        var valid = await ValidAuthorize(s, clientId, challenge);
        Assert.Equal("xyz", valid.State);
        Assert.Equal("Test client", valid.Client.Name);
        var code = s.IssueCode(valid, Urls);

        var token = await s.TokenAsync(CodeGrant(clientId, code, verifier), Urls, default);
        Assert.Null(token.Error);
        var access = token.Value!["access_token"]!.GetValue<string>();
        var refresh = token.Value["refresh_token"]!.GetValue<string>();
        Assert.Equal("Bearer", token.Value["token_type"]!.GetValue<string>());
        Assert.Equal(3600, token.Value["expires_in"]!.GetValue<long>());
        Assert.Equal("analytics", token.Value["scope"]!.GetValue<string>());

        Assert.True(s.TryValidateAccessToken(access, Urls, out _));
        Assert.False(s.TryValidateAccessToken(access, new OAuthUrls("https://other.example.com"), out var problem));
        Assert.Equal("issuer mismatch", problem);
        Assert.False(s.TryValidateAccessToken(refresh, Urls, out problem));
        Assert.Equal("wrong token type", problem);
        Assert.False(s.TryValidateAccessToken(code, Urls, out _));
        Assert.False(s.TryValidateAccessToken(access[..^3] + "abc", Urls, out problem));
        Assert.Equal("bad signature or format", problem);
        Assert.False(s.TryValidateAccessToken(ApiKey, Urls, out _));
        Assert.False(s.TryValidateAccessToken("", Urls, out _));

        // a code is single use
        var again = await s.TokenAsync(CodeGrant(clientId, code, verifier), Urls, default);
        Assert.Equal("invalid_grant", again.Error?.Error);

        // the access token expires, the refresh token rotates
        clock.Now += TimeSpan.FromMinutes(61);
        Assert.False(s.TryValidateAccessToken(access, Urls, out problem));
        Assert.Equal("expired", problem);

        var refreshed = await s.TokenAsync(RefreshGrant(clientId, refresh), Urls, default);
        Assert.Null(refreshed.Error);
        Assert.True(s.TryValidateAccessToken(refreshed.Value!["access_token"]!.GetValue<string>(), Urls, out _));
        var reuse = await s.TokenAsync(RefreshGrant(clientId, refresh), Urls, default);
        Assert.Equal("invalid_grant", reuse.Error?.Error);

        // the grant as a whole has an absolute lifetime
        var refresh2 = refreshed.Value["refresh_token"]!.GetValue<string>();
        clock.Now += TimeSpan.FromDays(90) - TimeSpan.FromMinutes(91); // 30 min before the grant ends
        var stillOk = await s.TokenAsync(RefreshGrant(clientId, refresh2), Urls, default);
        Assert.Null(stillOk.Error);
        Assert.Equal(1800, stillOk.Value!["expires_in"]!.GetValue<long>()); // capped at the grant's end
        clock.Now += TimeSpan.FromMinutes(31);
        var late = await s.TokenAsync(RefreshGrant(clientId, stillOk.Value["refresh_token"]!.GetValue<string>()), Urls, default);
        Assert.Equal("invalid_grant", late.Error?.Error);
    }

    [Fact]
    public async Task Bad_pkce_wrong_client_wrong_redirect_and_wrong_resource_are_rejected()
    {
        var s = NewServer();
        var clientId = RegisterPublic(s);
        var other = RegisterPublic(s, name: "Other");
        var (verifier, challenge) = Pkce();

        // unknown client or unregistered redirect: refuse without redirecting
        Assert.IsType<AuthorizeOutcome.Rejected>(await s.ValidateAuthorizeAsync(Authorize("nope", challenge), Urls, default));
        Assert.IsType<AuthorizeOutcome.Rejected>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, redirect: "https://evil.example/cb"), Urls, default));
        Assert.IsType<AuthorizeOutcome.Rejected>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, redirect: Redirect + "/../other"), Urls, default));

        // a trusted client with a bad request: the error travels back through the redirect, with the state
        var noPkce = Assert.IsType<AuthorizeOutcome.RedirectWithError>(await s.ValidateAuthorizeAsync(Authorize(clientId, "", method: null), Urls, default));
        Assert.Equal("invalid_request", noPkce.Error.Error);
        Assert.Equal("xyz", noPkce.State);
        Assert.Equal(Redirect, noPkce.RedirectUri);
        var plain = Assert.IsType<AuthorizeOutcome.RedirectWithError>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, method: "plain"), Urls, default));
        Assert.Equal("invalid_request", plain.Error.Error);
        var implicitFlow = Assert.IsType<AuthorizeOutcome.RedirectWithError>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, responseType: "token"), Urls, default));
        Assert.Equal("unsupported_response_type", implicitFlow.Error.Error);
        var target = Assert.IsType<AuthorizeOutcome.RedirectWithError>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, resource: "https://elsewhere.example/mcp"), Urls, default));
        Assert.Equal("invalid_target", target.Error.Error);

        // resource forms that mean the same thing
        Assert.IsType<AuthorizeOutcome.Valid>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, resource: "HTTPS://Gateway.Example.com/mcp/"), Urls, default));
        Assert.IsType<AuthorizeOutcome.Valid>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, resource: null), Urls, default));
        Assert.IsType<AuthorizeOutcome.Valid>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, redirect: null!) with { RedirectUri = null }, Urls, default)); // single registered URI → implied

        var code = s.IssueCode(await ValidAuthorize(s, clientId, challenge), Urls);
        Assert.Equal("invalid_grant", (await s.TokenAsync(CodeGrant(clientId, code, Pkce().Verifier), Urls, default)).Error?.Error);
        Assert.Equal("invalid_grant", (await s.TokenAsync(CodeGrant(other, code, verifier), Urls, default)).Error?.Error);
        Assert.Equal("invalid_grant", (await s.TokenAsync(CodeGrant(clientId, code, verifier, redirect: Redirect + "x"), Urls, default)).Error?.Error);
        Assert.Equal("invalid_target", (await s.TokenAsync(CodeGrant(clientId, code, verifier, resource: "https://elsewhere.example/mcp"), Urls, default)).Error?.Error);
        Assert.Equal("invalid_grant", (await s.TokenAsync(CodeGrant(clientId, code + "x", verifier), Urls, default)).Error?.Error);
        Assert.Equal("unsupported_grant_type", (await s.TokenAsync(CodeGrant(clientId, code, verifier) with { GrantType = "client_credentials" }, Urls, default)).Error?.Error);
        Assert.Equal("invalid_client", (await s.TokenAsync(CodeGrant("nope", code, verifier), Urls, default)).Error?.Error);
        Assert.Equal(401, (await s.TokenAsync(CodeGrant("nope", code, verifier), Urls, default)).Error?.Status);

        // none of the failed attempts consumed the code
        Assert.Null((await s.TokenAsync(CodeGrant(clientId, code, verifier), Urls, default)).Error);

        // a code minted for another deployment (same key, other origin) is not valid here
        var elsewhere = new OAuthUrls("https://other.example.com");
        var foreignCode = s.IssueCode(Assert.IsType<AuthorizeOutcome.Valid>(await s.ValidateAuthorizeAsync(Authorize(clientId, challenge, resource: null), elsewhere, default)), elsewhere);
        Assert.Equal("invalid_grant", (await s.TokenAsync(CodeGrant(clientId, foreignCode, verifier, resource: null), Urls, default)).Error?.Error);
    }

    [Fact]
    public async Task Confidential_client_must_present_its_secret()
    {
        var s = NewServer();
        var registration = s.Register(new JsonObject { ["redirect_uris"] = new JsonArray(Redirect) }); // RFC 7591 default = client_secret_basic
        Assert.Null(registration.Error);
        var clientId = registration.Value!["client_id"]!.GetValue<string>();
        var secret = registration.Value["client_secret"]!.GetValue<string>();
        Assert.Equal("client_secret_basic", registration.Value["token_endpoint_auth_method"]!.GetValue<string>());
        Assert.Equal(0, registration.Value["client_secret_expires_at"]!.GetValue<long>());

        var (verifier, challenge) = Pkce();
        var code = s.IssueCode(await ValidAuthorize(s, clientId, challenge), Urls);

        var noSecret = await s.TokenAsync(CodeGrant(clientId, code, verifier), Urls, default);
        Assert.Equal("invalid_client", noSecret.Error?.Error);
        Assert.Equal(401, noSecret.Error?.Status);
        Assert.Equal("invalid_client", (await s.TokenAsync(CodeGrant(clientId, code, verifier, secret: secret + "x"), Urls, default)).Error?.Error);
        Assert.Null((await s.TokenAsync(CodeGrant(clientId, code, verifier, secret: secret), Urls, default)).Error);

        // the secret is bound to the client id (both are deterministic from the signing key, nothing is stored)
        var another = s.Register(new JsonObject { ["redirect_uris"] = new JsonArray(Redirect) }).Value!;
        Assert.NotEqual(secret, another["client_secret"]!.GetValue<string>());
    }

    [Fact]
    public async Task Registration_validates_redirect_uris_and_auth_method_and_ids_are_self_describing()
    {
        var s = NewServer();
        Assert.Equal("invalid_client_metadata", s.Register(null).Error?.Error);
        Assert.Equal("invalid_redirect_uri", s.Register(new JsonObject()).Error?.Error);
        Assert.Equal("invalid_redirect_uri", s.Register(new JsonObject { ["redirect_uris"] = new JsonArray() }).Error?.Error);
        Assert.Equal("invalid_redirect_uri", s.Register(new JsonObject { ["redirect_uris"] = new JsonArray("http://client.example/cb") }).Error?.Error);
        Assert.Equal("invalid_redirect_uri", s.Register(new JsonObject { ["redirect_uris"] = new JsonArray("https://client.example/cb#frag") }).Error?.Error);
        Assert.Equal("invalid_redirect_uri", s.Register(new JsonObject { ["redirect_uris"] = new JsonArray("not a uri") }).Error?.Error);
        Assert.Equal("invalid_redirect_uri", s.Register(new JsonObject { ["redirect_uris"] = new JsonArray(Enumerable.Range(0, 11).Select(i => (JsonNode)$"https://c.example/{i}").ToArray()) }).Error?.Error);
        Assert.Equal("invalid_client_metadata", s.Register(new JsonObject { ["redirect_uris"] = new JsonArray(Redirect), ["token_endpoint_auth_method"] = "private_key_jwt" }).Error?.Error);
        Assert.Null(s.Register(new JsonObject { ["redirect_uris"] = new JsonArray("http://localhost:3000/cb", "http://127.0.0.1:3000/cb") }).Error);

        var ok = s.Register(new JsonObject { ["redirect_uris"] = new JsonArray(Redirect), ["token_endpoint_auth_method"] = "none", ["client_name"] = "  Claude  " });
        Assert.Null(ok.Error);
        Assert.Equal("Claude", ok.Value!["client_name"]!.GetValue<string>());
        Assert.Null(ok.Value["client_secret"]);
        Assert.Equal("analytics", ok.Value["scope"]!.GetValue<string>());
        var clientId = ok.Value["client_id"]!.GetValue<string>();

        // another instance with the same key knows the client — the registration lives in the id
        var client = await NewServer().ResolveClientAsync(clientId, default);
        Assert.NotNull(client);
        Assert.Equal("Claude", client.Name);
        Assert.True(client.IsPublic);
        Assert.False(client.IsMetadataDocument);
        Assert.Equal([Redirect], client.RedirectUris);

        Assert.Null(await s.ResolveClientAsync(clientId[..^2] + "zz", default));
        Assert.Null(await s.ResolveClientAsync(clientId.Replace("eyJ", "eyK"), default));
        Assert.Null(await s.ResolveClientAsync("", default));
        Assert.Null(await NewServer(configure: o => o.AllowDynamicRegistration = false).ResolveClientAsync(clientId, default));

        // a different signing key = a different authorization server; its ids mean nothing here
        var foreign = NewServer(configure: o => o.SigningKey = "another-secret-value-0123456789");
        Assert.Null(await foreign.ResolveClientAsync(clientId, default));
    }

    [Fact]
    public void Client_metadata_documents_are_validated()
    {
        const string url = "https://chatgpt.com/oauth/client.json";
        var client = OAuthClient.FromMetadataDocument(url, JsonNode.Parse(ChatGptClientJson) as JsonObject, out _);
        Assert.NotNull(client);
        Assert.Equal("ChatGPT", client.Name);
        Assert.True(client.IsPublic);
        Assert.True(client.IsMetadataDocument);
        Assert.True(client.AllowsRedirect("https://chatgpt.com/connector_platform_oauth_redirect"));
        Assert.Equal("chatgpt.com", client.RedirectHosts);
        Assert.False(client.HasLoopbackRedirect);

        var mismatched = (JsonObject)JsonNode.Parse(ChatGptClientJson)!;
        mismatched["client_id"] = "https://chatgpt.com/oauth/other.json";
        Assert.Null(OAuthClient.FromMetadataDocument(url, mismatched, out var problem));
        Assert.Contains("client_id", problem);

        var jwtOnly = (JsonObject)JsonNode.Parse(ChatGptClientJson)!;
        jwtOnly.Remove("token_endpoint_auth_methods_supported");
        Assert.Null(OAuthClient.FromMetadataDocument(url, jwtOnly, out problem));
        Assert.Contains("none", problem);

        var noRedirects = (JsonObject)JsonNode.Parse(ChatGptClientJson)!;
        noRedirects["redirect_uris"] = new JsonArray("http://chatgpt.com/cb");
        Assert.Null(OAuthClient.FromMetadataDocument(url, noRedirects, out _));
        Assert.Null(OAuthClient.FromMetadataDocument(url, null, out _));

        // which ids are treated (and fetched) as metadata documents at all
        Assert.True(OAuthClient.IsMetadataDocumentUrl(url, out var parsed));
        Assert.Equal("chatgpt.com", parsed.Host);
        Assert.False(OAuthClient.IsMetadataDocumentUrl("https://chatgpt.com", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("https://chatgpt.com/", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("http://chatgpt.com/oauth/client.json", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("https://chatgpt.com/oauth/client.json#x", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("https://10.0.0.1/client.json", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("https://[::1]/client.json", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("https://localhost/client.json", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("https://api/client.json", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("https://api.internal/client.json", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("https://user:pw@chatgpt.com/client.json", out _));
        Assert.False(OAuthClient.IsMetadataDocumentUrl("eyJhbGciOiJIUzI1NiJ9.e30.x", out _));
    }

    [Fact]
    public async Task Chatgpt_style_metadata_document_client_authorizes_as_a_public_client()
    {
        const string url = "https://chatgpt.com/oauth/client.json";
        const string chatGptRedirect = "https://chatgpt.com/connector_platform_oauth_redirect";
        var documents = new StubDocuments(id => id == url ? OAuthClient.FromMetadataDocument(url, JsonNode.Parse(ChatGptClientJson) as JsonObject, out _) : null);
        var s = NewServer(documents: documents);
        var (verifier, challenge) = Pkce();

        var valid = await ValidAuthorize(s, url, challenge, redirect: chatGptRedirect);
        Assert.Equal("ChatGPT", valid.Client.Name);
        var code = s.IssueCode(valid, Urls);
        var token = await s.TokenAsync(CodeGrant(url, code, verifier, redirect: chatGptRedirect), Urls, default);
        Assert.Null(token.Error);
        Assert.True(s.TryValidateAccessToken(token.Value!["access_token"]!.GetValue<string>(), Urls, out _));
        Assert.Null((await s.TokenAsync(RefreshGrant(url, token.Value["refresh_token"]!.GetValue<string>()), Urls, default)).Error);

        Assert.IsType<AuthorizeOutcome.Rejected>(await s.ValidateAuthorizeAsync(Authorize(url, challenge, redirect: "https://evil.example/cb"), Urls, default));
        Assert.IsType<AuthorizeOutcome.Rejected>(await s.ValidateAuthorizeAsync(Authorize("https://chatgpt.com/oauth/unknown.json", challenge, redirect: chatGptRedirect), Urls, default));

        var off = NewServer(documents: documents, configure: o => o.AllowClientMetadataDocuments = false);
        Assert.IsType<AuthorizeOutcome.Rejected>(await off.ValidateAuthorizeAsync(Authorize(url, challenge, redirect: chatGptRedirect), Urls, default));
        Assert.False(off.AuthorizationServerMetadata(Urls)["client_id_metadata_document_supported"]!.GetValue<bool>());
    }

    [Fact]
    public void Metadata_documents_and_challenges_point_at_this_origin()
    {
        var s = NewServer();
        var resource = s.ProtectedResourceMetadata(Urls);
        Assert.Equal("https://gateway.example.com/mcp", resource["resource"]!.GetValue<string>());
        Assert.Equal("https://gateway.example.com", resource["authorization_servers"]![0]!.GetValue<string>());
        Assert.Equal("analytics", resource["scopes_supported"]![0]!.GetValue<string>());

        var server = s.AuthorizationServerMetadata(Urls);
        Assert.Equal("https://gateway.example.com", server["issuer"]!.GetValue<string>());
        Assert.Equal("https://gateway.example.com/mcp/oauth/authorize", server["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("https://gateway.example.com/mcp/oauth/token", server["token_endpoint"]!.GetValue<string>());
        Assert.Equal("https://gateway.example.com/mcp/oauth/register", server["registration_endpoint"]!.GetValue<string>());
        Assert.Equal(["S256"], server["code_challenge_methods_supported"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("none", server["token_endpoint_auth_methods_supported"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(server["client_id_metadata_document_supported"]!.GetValue<bool>());
        Assert.True(server["authorization_response_iss_parameter_supported"]!.GetValue<bool>());
        Assert.Null(NewServer(configure: o => o.AllowDynamicRegistration = false).AuthorizationServerMetadata(Urls)["registration_endpoint"]);

        Assert.Equal("Bearer resource_metadata=\"https://gateway.example.com/.well-known/oauth-protected-resource\", scope=\"analytics\"", McpHttpHost.Challenge(Urls, null, null));
        Assert.Equal("Bearer error=\"invalid_token\", error_description=\"expired\", resource_metadata=\"https://gateway.example.com/.well-known/oauth-protected-resource\", scope=\"analytics\"", McpHttpHost.Challenge(Urls, "invalid_token", "expired"));
        Assert.Equal("Bearer", McpHttpHost.Challenge(null, null, null));
        Assert.Equal("Bearer error=\"invalid_token\", error_description=\"a 'b' c\"", McpHttpHost.Challenge(null, "invalid_token", "a \"b\"\r\nc"));

        Assert.False(NewServer(configure: o => o.Enabled = false).Active);
        Assert.False(new OAuthServer(new McpHttpOptions(), "short", new StubDocuments(_ => null), TimeProvider.System, NullLogger<OAuthServer>.Instance).Active);
        Assert.True(s.Active);

        Assert.True(McpHttpHost.IsProtected("/mcp"));
        Assert.True(McpHttpHost.IsProtected("/mcp/"));
        Assert.True(McpHttpHost.IsProtected("/mcp/sse"));
        Assert.False(McpHttpHost.IsProtected("/mcp/oauth/token"));
        Assert.False(McpHttpHost.IsProtected("/mcp/oauth/authorize"));
        Assert.False(McpHttpHost.IsProtected("/mcp/.well-known/oauth-protected-resource"));
        Assert.False(McpHttpHost.IsProtected("/.well-known/oauth-authorization-server"));
        Assert.False(McpHttpHost.IsProtected("/healthz"));
    }

    [Fact]
    public async Task Http_host_challenges_discovers_registers_consents_and_accepts_tokens()
    {
        await using var app = McpHttpHost.Build(
        [
            "--urls", "http://127.0.0.1:0",
            "--Mcp:ClientApiKey=" + ApiKey,
            "--KickGateway:BaseUrl=http://127.0.0.1:9",
            "--KickGateway:ApiKey=" + ApiKey,
            "--Logging:LogLevel:Default=Warning",
        ]);
        await app.StartAsync();
        try
        {
            var baseUrl = app.Urls.First().TrimEnd('/');
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl + "/") };
            var resource = baseUrl + "/mcp";

            // 1. no credentials → 401 that says where the metadata is (and no error code, per RFC 6750)
            using var anonymous = await http.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            var challenge = anonymous.Headers.WwwAuthenticate.ToString();
            Assert.Contains($"resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"", challenge);
            Assert.Contains("scope=\"analytics\"", challenge);
            Assert.DoesNotContain("error=", challenge);

            // 2. discovery at every path a client might probe; URLs derived from the request (no PublicBaseUrl configured)
            var resourceMetadata = await GetJsonAsync(http, "/.well-known/oauth-protected-resource");
            Assert.Equal(resource, resourceMetadata["resource"]!.GetValue<string>());
            Assert.Equal(baseUrl, resourceMetadata["authorization_servers"]![0]!.GetValue<string>());
            Assert.Equal(resourceMetadata.ToJsonString(), (await GetJsonAsync(http, "/.well-known/oauth-protected-resource/mcp")).ToJsonString());
            var serverMetadata = await GetJsonAsync(http, "/.well-known/oauth-authorization-server");
            Assert.Equal(baseUrl, serverMetadata["issuer"]!.GetValue<string>());
            Assert.Equal(serverMetadata.ToJsonString(), (await GetJsonAsync(http, "/.well-known/openid-configuration")).ToJsonString());
            Assert.Equal(serverMetadata.ToJsonString(), (await GetJsonAsync(http, "/mcp/.well-known/oauth-authorization-server")).ToJsonString());

            // forwarded headers (what Traefik sends) decide the advertised origin
            using var forwarded = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-protected-resource");
            forwarded.Headers.Add("X-Forwarded-Proto", "https");
            forwarded.Headers.Add("X-Forwarded-Host", "gateway.example.com");
            using var forwardedResponse = await http.SendAsync(forwarded);
            Assert.Equal("https://gateway.example.com/mcp", (JsonNode.Parse(await forwardedResponse.Content.ReadAsStringAsync()) as JsonObject)!["resource"]!.GetValue<string>());

            // 3. dynamic registration
            using var registration = await http.PostAsync(serverMetadata["registration_endpoint"]!.GetValue<string>(),
                new StringContent("""{"client_name":"Inspector","redirect_uris":["http://localhost:6274/oauth/callback"],"token_endpoint_auth_method":"none"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
            var clientId = (JsonNode.Parse(await registration.Content.ReadAsStringAsync()) as JsonObject)!["client_id"]!.GetValue<string>();
            using var badRegistration = await http.PostAsync(serverMetadata["registration_endpoint"]!.GetValue<string>(), new StringContent("""{"redirect_uris":["ftp://x"]}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, badRegistration.StatusCode);
            Assert.Equal("invalid_redirect_uri", (JsonNode.Parse(await badRegistration.Content.ReadAsStringAsync()) as JsonObject)!["error"]!.GetValue<string>());

            // 4. the consent page
            var (verifier, codeChallenge) = Pkce();
            var authorizeParams = new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = "http://localhost:6274/oauth/callback",
                ["response_type"] = "code",
                ["state"] = "st4te",
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
                ["resource"] = resource,
            };
            using var page = await http.GetAsync(QueryHelpers.AddQueryString(serverMetadata["authorization_endpoint"]!.GetValue<string>(), authorizeParams));
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("Inspector", html);
            Assert.Contains("localhost", html);
            Assert.Contains("name=\"api_key\"", html);
            Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
            Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());

            using var unknownClientPage = await http.GetAsync(OAuthEndpoints.AuthorizePath + "?client_id=nope&redirect_uri=https%3A%2F%2Fevil.example%2Fcb&response_type=code");
            Assert.Equal(HttpStatusCode.BadRequest, unknownClientPage.StatusCode); // no redirect to an untrusted URI

            // 5. wrong key → 401 page; deny → access_denied; right key → code + state + iss on the registered redirect
            var form = authorizeParams.ToDictionary(kv => kv.Key, kv => kv.Value!);
            form["decision"] = "approve";
            form["api_key"] = "wrong";
            using var wrongKey = await http.PostAsync(OAuthEndpoints.AuthorizePath, new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
            Assert.Contains("not accepted", await wrongKey.Content.ReadAsStringAsync());

            form["decision"] = "deny";
            using var denied = await http.PostAsync(OAuthEndpoints.AuthorizePath, new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.Found, denied.StatusCode);
            Assert.Equal("access_denied", QueryHelpers.ParseQuery(denied.Headers.Location!.Query)["error"]);

            form["decision"] = "approve";
            form["api_key"] = ApiKey;
            using var approved = await http.PostAsync(OAuthEndpoints.AuthorizePath, new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.Found, approved.StatusCode);
            var location = approved.Headers.Location!;
            Assert.StartsWith("http://localhost:6274/oauth/callback?", location.ToString());
            var callback = QueryHelpers.ParseQuery(location.Query);
            Assert.Equal("st4te", callback["state"]);
            Assert.Equal(baseUrl, callback["iss"]);
            var code = callback["code"].ToString();
            Assert.NotEmpty(code);

            // 6. the token endpoint (form-encoded, no-store)
            var tokenEndpoint = serverMetadata["token_endpoint"]!.GetValue<string>();
            using var tokenResponse = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["client_id"] = clientId,
                ["redirect_uri"] = "http://localhost:6274/oauth/callback",
                ["resource"] = resource,
            }));
            Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
            Assert.Equal("no-store", tokenResponse.Headers.CacheControl?.ToString());
            var tokens = (JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync()) as JsonObject)!;
            var access = tokens["access_token"]!.GetValue<string>();
            Assert.Equal("Bearer", tokens["token_type"]!.GetValue<string>());

            // 7. /mcp takes the access token and the API key (both header styles), nothing else
            using (var ok = await InitializeAsync(http, "Authorization", "Bearer " + access)) Assert.NotEqual(HttpStatusCode.Unauthorized, ok.StatusCode);
            using (var ok = await InitializeAsync(http, "Authorization", "Bearer " + ApiKey)) Assert.NotEqual(HttpStatusCode.Unauthorized, ok.StatusCode);
            using (var ok = await InitializeAsync(http, "X-Api-Key", ApiKey)) Assert.NotEqual(HttpStatusCode.Unauthorized, ok.StatusCode);
            using (var bad = await InitializeAsync(http, "Authorization", "Bearer " + access[..^4] + "zzzz"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
                Assert.Contains("error=\"invalid_token\"", bad.Headers.WwwAuthenticate.ToString());
                Assert.Contains("resource_metadata=", bad.Headers.WwwAuthenticate.ToString());
            }
            using (var bad = await InitializeAsync(http, "X-Api-Key", "wrong")) Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

            // 8. refresh, and errors in OAuth shape
            using var refreshed = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens["refresh_token"]!.GetValue<string>(),
                ["client_id"] = clientId,
            }));
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

            using var replay = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["client_id"] = clientId,
            }));
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.Equal("invalid_grant", (JsonNode.Parse(await replay.Content.ReadAsStringAsync()) as JsonObject)!["error"]!.GetValue<string>());

            using var unknownClient = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["client_id"] = "nope",
            }));
            Assert.Equal(HttpStatusCode.Unauthorized, unknownClient.StatusCode);
            Assert.Equal("invalid_client", (JsonNode.Parse(await unknownClient.Content.ReadAsStringAsync()) as JsonObject)!["error"]!.GetValue<string>());

            using var notForm = await http.PostAsync(tokenEndpoint, new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, notForm.StatusCode);

            // 9. a confidential client authenticates with Basic
            using var confidential = await http.PostAsync(serverMetadata["registration_endpoint"]!.GetValue<string>(),
                new StringContent("""{"client_name":"Server app","redirect_uris":["https://app.example/cb"]}""", Encoding.UTF8, "application/json"));
            var confidentialClient = (JsonNode.Parse(await confidential.Content.ReadAsStringAsync()) as JsonObject)!;
            var (verifier2, challenge2) = Pkce();
            using var approved2 = await http.PostAsync(OAuthEndpoints.AuthorizePath, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = confidentialClient["client_id"]!.GetValue<string>(),
                ["redirect_uri"] = "https://app.example/cb",
                ["response_type"] = "code",
                ["code_challenge"] = challenge2,
                ["code_challenge_method"] = "S256",
                ["decision"] = "approve",
                ["api_key"] = ApiKey,
            }));
            Assert.Equal(HttpStatusCode.Found, approved2.StatusCode);
            var code2 = QueryHelpers.ParseQuery(approved2.Headers.Location!.Query)["code"].ToString();
            using var basicRequest = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code2, ["code_verifier"] = verifier2, ["redirect_uri"] = "https://app.example/cb",
                }),
            };
            basicRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Uri.EscapeDataString(confidentialClient["client_id"]!.GetValue<string>()) + ":" + Uri.EscapeDataString(confidentialClient["client_secret"]!.GetValue<string>()))));
            using var basicResponse = await http.SendAsync(basicRequest);
            Assert.Equal(HttpStatusCode.OK, basicResponse.StatusCode);

            using var healthz = await http.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, healthz.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<JsonObject> GetJsonAsync(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject)!;
    }

    private static async Task<HttpResponseMessage> InitializeAsync(HttpClient http, string header, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(header, value);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }
}
