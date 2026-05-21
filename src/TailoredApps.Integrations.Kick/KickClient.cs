using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick.Internal;
using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.Integrations.Kick;

public class KickClient : IKickClient
{
    public const string AuthHttpClientName = "KickAuth";
    public const string ApiHttpClientName = "KickApi";

    private readonly IHttpClientFactory _factory;
    private readonly KickGlobalDefaults _defaults;
    private readonly ILogger<KickClient> _log;

    public KickClient(IHttpClientFactory factory, IOptions<KickGlobalDefaults> defaults, ILogger<KickClient> log)
    {
        _factory = factory;
        _defaults = defaults.Value;
        _log = log;
    }

    public async Task<KickTokenResult> ExchangeAuthorizationCodeAsync(string clientId, string clientSecret, string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(AuthHttpClientName);
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("grant_type", "authorization_code"),
            new KeyValuePair<string,string>("client_id", clientId),
            new KeyValuePair<string,string>("client_secret", clientSecret),
            new KeyValuePair<string,string>("redirect_uri", redirectUri),
            new KeyValuePair<string,string>("code_verifier", codeVerifier),
            new KeyValuePair<string,string>("code", code)
        });

        var response = await http.PostAsync("/oauth/token", form, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogError("Kick token exchange failed (client {Client}): {Status} {Body}", clientId, response.StatusCode, raw);
            return new KickTokenResult(false, null, null, 0, null, null, $"HTTP {(int)response.StatusCode}: {raw}");
        }
        return ParseTokenResponse(raw);
    }

    public async Task<KickTokenResult> RefreshAccessTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(AuthHttpClientName);
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("grant_type", "refresh_token"),
            new KeyValuePair<string,string>("client_id", clientId),
            new KeyValuePair<string,string>("client_secret", clientSecret),
            new KeyValuePair<string,string>("refresh_token", refreshToken)
        });

        var response = await http.PostAsync("/oauth/token", form, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return new KickTokenResult(false, null, null, 0, null, null, $"HTTP {(int)response.StatusCode}: {raw}");
        return ParseTokenResponse(raw);
    }

    public async Task<KickPublicKey> GetPublicKeyAsync(CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ApiHttpClientName);
        var response = await http.GetAsync("/public/v1/public-key", ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        var pem = doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("public_key", out var pk)
            ? pk.GetString() ?? ""
            : doc.RootElement.TryGetProperty("public_key", out var pkRoot) ? pkRoot.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(pem))
            throw new InvalidOperationException($"Kick public key response missing public_key field: {raw}");
        return new KickPublicKey(pem, DateTime.UtcNow);
    }

    public async Task<KickUserInfo?> GetCurrentUserAsync(string accessToken, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ApiHttpClientName);
        var req = new HttpRequestMessage(HttpMethod.Get, "/public/v1/users");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await http.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode) return null;
        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;
        var first = data[0];
        var userId = first.ReadAsString("user_id");
        var name = first.ReadAsString("name");
        var email = first.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return new KickUserInfo(userId, name, email);
    }

    public async Task<IReadOnlyList<KickSubscriptionInfo>> ListSubscriptionsAsync(string accessToken, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ApiHttpClientName);
        var req = new HttpRequestMessage(HttpMethod.Get, "/public/v1/events/subscriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<KickSubscriptionInfo>();

        var list = new List<KickSubscriptionInfo>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.ReadAsString("id");
            var ev = item.ReadAsString("event");
            var ver = item.ReadAsString("version");
            var method = item.ReadAsString("method", fallback: "webhook");
            var created = item.TryGetProperty("created_at", out var cEl) && cEl.TryGetDateTime(out var dt) ? dt : DateTime.UtcNow;
            list.Add(new KickSubscriptionInfo(id, ev, ver, method, created));
        }
        return list;
    }

    public async Task<KickSubscriptionInfo?> CreateSubscriptionAsync(string accessToken, string eventName, int version, string method, string? broadcasterUserId, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ApiHttpClientName);
        var req = new HttpRequestMessage(HttpMethod.Post, "/public/v1/events/subscriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // version goes on the wire as JSON number — Kick rejects strings.
        var events = new[] { new { name = eventName, version } };
        object body;
        if (broadcasterUserId is null)
            body = new { events, method };
        else if (long.TryParse(broadcasterUserId, out var numericId))
            body = new { events, method, broadcaster_user_id = numericId };
        else
            body = new { events, method, broadcaster_user_id = broadcasterUserId };
        req.Content = JsonContent.Create(body);

        var response = await http.SendAsync(req, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("Kick CreateSubscription failed for {Event}: {Status} {Body}", eventName, response.StatusCode, raw);
            return null;
        }
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            _log.LogWarning("Kick CreateSubscription returned empty data for {Event}: {Body}", eventName, raw);
            return null;
        }
        var first = data[0];
        var id = first.ReadAsString("subscription_id");
        _log.LogInformation("Kick CreateSubscription OK — event={Event} version={Version} method={Method} id={Id}",
            eventName, version, method, id);
        return new KickSubscriptionInfo(id, eventName, version.ToString(), method, DateTime.UtcNow);
    }

    public async Task<bool> DeleteSubscriptionAsync(string accessToken, string subscriptionId, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ApiHttpClientName);
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/public/v1/events/subscriptions?id={Uri.EscapeDataString(subscriptionId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await http.SendAsync(req, ct);
        return response.IsSuccessStatusCode;
    }

    private static KickTokenResult ParseTokenResponse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        var expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var sec) ? sec : 0;
        var scope = root.TryGetProperty("scope", out var s) ? s.GetString() : null;
        var tokenType = root.TryGetProperty("token_type", out var t) ? t.GetString() : "Bearer";
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
            return new KickTokenResult(false, null, null, 0, null, null, "missing access_token or refresh_token");
        return new KickTokenResult(true, access, refresh, expires, scope, tokenType, null);
    }
}
