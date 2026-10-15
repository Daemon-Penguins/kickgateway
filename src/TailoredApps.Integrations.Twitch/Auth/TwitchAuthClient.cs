using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TailoredApps.Integrations.Twitch.Auth;

public sealed class TwitchAuthClient : ITwitchAuthClient
{
    private static readonly TimeSpan AppTokenSafety = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _factory;
    private readonly TwitchOptions _o;
    private readonly ILogger<TwitchAuthClient> _log;
    private readonly SemaphoreSlim _appTokenGate = new(1, 1);
    private string? _appToken;
    private DateTime _appTokenExpiresAt;

    public TwitchAuthClient(IHttpClientFactory factory, IOptions<TwitchOptions> options, ILogger<TwitchAuthClient> log)
    {
        _factory = factory;
        _o = options.Value;
        _log = log;
    }

    public string BuildAuthorizationUrl(string redirectUri, string scopes, string state, bool forceVerify = true)
    {
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _o.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scopes,
            ["state"] = state,
        };
        if (forceVerify) q["force_verify"] = "true";
        var query = string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{_o.AuthBaseUrl.TrimEnd('/')}/oauth2/authorize?{query}";
    }

    public Task<TwitchTokenResult> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _o.ClientId,
            ["client_secret"] = _o.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        }, ct);

    public Task<TwitchTokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _o.ClientId,
            ["client_secret"] = _o.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    public async Task<TwitchTokenValidation?> ValidateAsync(string accessToken, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ServiceCollectionExtensions.AuthHttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, "oauth2/validate");
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Twitch token validation returned HTTP {Status}", (int)resp.StatusCode);
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        return new TwitchTokenValidation(
            Str(r, "client_id"), Str(r, "login"), Str(r, "user_id"), Scopes(r), Int(r, "expires_in"));
    }

    public async Task<string?> GetAppAccessTokenAsync(CancellationToken ct = default)
    {
        if (!_o.IsConfigured) return null;
        if (_appToken is not null && DateTime.UtcNow < _appTokenExpiresAt) return _appToken;

        await _appTokenGate.WaitAsync(ct);
        try
        {
            if (_appToken is not null && DateTime.UtcNow < _appTokenExpiresAt) return _appToken;
            var result = await TokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = _o.ClientId,
                ["client_secret"] = _o.ClientSecret,
                ["grant_type"] = "client_credentials",
            }, ct);
            if (!result.Success || result.AccessToken is null)
            {
                _log.LogError("Twitch app access token request failed: {Error}", result.Error);
                return null;
            }
            _appToken = result.AccessToken;
            _appTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, result.ExpiresInSec)) - AppTokenSafety;
            return _appToken;
        }
        finally
        {
            _appTokenGate.Release();
        }
    }

    private async Task<TwitchTokenResult> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var http = _factory.CreateClient(ServiceCollectionExtensions.AuthHttpClientName);
        using var content = new FormUrlEncodedContent(form);
        using var resp = await http.PostAsync("oauth2/token", content, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // The body names the problem ("Invalid refresh token", "invalid client") without leaking secrets.
            return new TwitchTokenResult(false, null, null, 0, [], $"HTTP {(int)resp.StatusCode}: {Truncate(raw, 300)}", (int)resp.StatusCode);
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            var access = Str(r, "access_token");
            if (access.Length == 0) return new TwitchTokenResult(false, null, null, 0, [], "no access_token in response", (int)resp.StatusCode);
            var refresh = Str(r, "refresh_token");
            return new TwitchTokenResult(true, access, refresh.Length == 0 ? null : refresh, Int(r, "expires_in"), Scopes(r), null, (int)resp.StatusCode);
        }
        catch (JsonException ex)
        {
            return new TwitchTokenResult(false, null, null, 0, [], "unparseable token response: " + ex.Message, (int)resp.StatusCode);
        }
    }

    private static string[] Scopes(JsonElement r)
    {
        if (!r.TryGetProperty("scope", out var s)) return [];
        if (s.ValueKind == JsonValueKind.Array)
            return s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
        if (s.ValueKind == JsonValueKind.String)
            return (s.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (r.TryGetProperty("scopes", out var ss) && ss.ValueKind == JsonValueKind.Array)
            return ss.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
        return [];
    }

    private static string Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
