using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TailoredApps.Integrations.Twitch.Helix;

public sealed class TwitchHelixClient : ITwitchHelixClient
{
    private const int MaxIdsPerRequest = 100;

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<TwitchHelixClient> _log;

    public TwitchHelixClient(IHttpClientFactory factory, ILogger<TwitchHelixClient> log)
    {
        _factory = factory;
        _log = log;
    }

    public Task<IReadOnlyList<TwitchUser>> GetUsersByLoginAsync(string accessToken, IEnumerable<string> logins, CancellationToken ct = default) =>
        UsersAsync(accessToken, "login", logins.Select(l => l.Trim().ToLowerInvariant()), ct);

    public Task<IReadOnlyList<TwitchUser>> GetUsersByIdAsync(string accessToken, IEnumerable<string> userIds, CancellationToken ct = default) =>
        UsersAsync(accessToken, "id", userIds, ct);

    private async Task<IReadOnlyList<TwitchUser>> UsersAsync(string accessToken, string param, IEnumerable<string> values, CancellationToken ct)
    {
        var result = new List<TwitchUser>();
        foreach (var batch in Batches(values))
        {
            var query = string.Join("&", batch.Select(v => $"{param}={Uri.EscapeDataString(v)}"));
            using var doc = await GetAsync(accessToken, "users?" + query, ct);
            if (doc is null) continue;
            foreach (var u in Data(doc))
                result.Add(new TwitchUser(Str(u, "id"), Str(u, "login"), Str(u, "display_name"), Str(u, "profile_image_url"), Str(u, "broadcaster_type")));
        }
        return result;
    }

    public async Task<IReadOnlyList<TwitchStream>> GetStreamsAsync(string accessToken, IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var result = new List<TwitchStream>();
        foreach (var batch in Batches(userIds))
        {
            var query = "first=100&" + string.Join("&", batch.Select(v => $"user_id={Uri.EscapeDataString(v)}"));
            using var doc = await GetAsync(accessToken, "streams?" + query, ct);
            if (doc is null) continue;
            foreach (var s in Data(doc))
            {
                result.Add(new TwitchStream(
                    Str(s, "id"), Str(s, "user_id"), Str(s, "user_login"), Str(s, "user_name"),
                    Str(s, "game_id"), Str(s, "game_name"), Str(s, "type"), Str(s, "title"),
                    Int(s, "viewer_count"), Date(s, "started_at") ?? DateTime.UtcNow, Str(s, "language"), Bool(s, "is_mature")));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<TwitchVideo>> GetVideosAsync(string accessToken, string userId, string type = "archive", int first = 5, CancellationToken ct = default)
    {
        var result = new List<TwitchVideo>();
        if (string.IsNullOrWhiteSpace(userId)) return result;
        var query = $"user_id={Uri.EscapeDataString(userId.Trim())}&type={Uri.EscapeDataString(type)}&first={Math.Clamp(first, 1, 100)}";
        using var doc = await GetAsync(accessToken, "videos?" + query, ct);
        if (doc is null) return result;
        foreach (var v in Data(doc))
        {
            result.Add(new TwitchVideo(
                Str(v, "id"), Str(v, "stream_id"), Str(v, "user_id"), Str(v, "user_login"), Str(v, "title"),
                Date(v, "created_at") ?? DateTime.UtcNow, Date(v, "published_at"), Str(v, "url"), Str(v, "type"), Str(v, "duration")));
        }
        return result;
    }

    public async Task<EventSubCreateResult> CreateEventSubSubscriptionAsync(string accessToken, string type, string version,
        IReadOnlyDictionary<string, string> condition, string websocketSessionId, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ServiceCollectionExtensions.HelixHttpClientName);
        var body = JsonSerializer.Serialize(new
        {
            type,
            version,
            condition,
            transport = new { method = "websocket", session_id = websocketSessionId },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "eventsub/subscriptions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new EventSubCreateResult(false, null, (int)resp.StatusCode, Truncate(raw, 300), 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var sub = Data(doc).Select(ReadSubscription).FirstOrDefault();
            return new EventSubCreateResult(sub is not null, sub, (int)resp.StatusCode, sub is null ? "empty data" : null,
                Int(root, "total_cost"), Int(root, "max_total_cost"));
        }
        catch (JsonException ex)
        {
            return new EventSubCreateResult(false, null, (int)resp.StatusCode, "unparseable response: " + ex.Message, 0, 0);
        }
    }

    public async Task<IReadOnlyList<EventSubSubscription>> ListEventSubSubscriptionsAsync(string accessToken, string? status = null, CancellationToken ct = default)
    {
        var list = new List<EventSubSubscription>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var path = "eventsub/subscriptions";
            var q = new List<string>();
            if (!string.IsNullOrEmpty(status)) q.Add("status=" + Uri.EscapeDataString(status));
            if (cursor is not null) q.Add("after=" + Uri.EscapeDataString(cursor));
            if (q.Count > 0) path += "?" + string.Join("&", q);
            using var doc = await GetAsync(accessToken, path, ct);
            if (doc is null) break;
            list.AddRange(Data(doc).Select(ReadSubscription));
            cursor = doc.RootElement.TryGetProperty("pagination", out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "cursor") : "";
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return list;
    }

    public async Task<bool> DeleteEventSubSubscriptionAsync(string accessToken, string subscriptionId, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ServiceCollectionExtensions.HelixHttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Delete, "eventsub/subscriptions?id=" + Uri.EscapeDataString(subscriptionId));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound;
    }

    private async Task<JsonDocument?> GetAsync(string accessToken, string path, CancellationToken ct)
    {
        var http = _factory.CreateClient(ServiceCollectionExtensions.HelixHttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Rate limited (429) or unauthorized (401): the caller's loop retries on its own schedule.
            _log.LogWarning("Helix GET {Path} returned HTTP {Status}: {Body}", path.Split('?')[0], (int)resp.StatusCode, Truncate(raw, 200));
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new TwitchUnauthorizedException(path);
            return null;
        }
        try { return JsonDocument.Parse(raw); }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Helix GET {Path}: unparseable JSON", path.Split('?')[0]);
            return null;
        }
    }

    private static EventSubSubscription ReadSubscription(JsonElement s)
    {
        var transport = s.TryGetProperty("transport", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        var condition = s.TryGetProperty("condition", out var c) ? c.GetRawText() : "{}";
        return new EventSubSubscription(Str(s, "id"), Str(s, "status"), Str(s, "type"), Str(s, "version"), Int(s, "cost"), condition,
            transport.ValueKind == JsonValueKind.Object ? NullIfEmpty(Str(transport, "session_id")) : null,
            Date(s, "created_at") ?? DateTime.UtcNow);
    }

    private static IEnumerable<JsonElement> Data(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray() : [];

    private static IEnumerable<List<string>> Batches(IEnumerable<string> values)
    {
        var batch = new List<string>(MaxIdsPerRequest);
        foreach (var v in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal))
        {
            batch.Add(v);
            if (batch.Count == MaxIdsPerRequest)
            {
                yield return batch;
                batch = new List<string>(MaxIdsPerRequest);
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static string Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : ""
            : "";

    private static int Int(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static bool Bool(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? Date(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var dt)
            ? (dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime())
            : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}

/// <summary>Helix answered 401: the token is expired or revoked. Callers refresh (or re-authorize) and retry.</summary>
public sealed class TwitchUnauthorizedException(string path) : Exception($"Twitch Helix rejected the token on {path.Split('?')[0]}");
