using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace TailoredApps.KickGateway.Mcp;

/// <summary>Configuration section <c>KickGateway</c> (env: <c>KickGateway__BaseUrl</c>, <c>KickGateway__ApiKey</c>).</summary>
public sealed class GatewayOptions
{
    public const string SectionName = "KickGateway";

    /// <summary>Root URL of the gateway Api, e.g. <c>https://gateway.example.com</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5286";

    /// <summary>Must equal the gateway's <c>Analytics:ApiKey</c>.</summary>
    public string ApiKey { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 90;
}

/// <summary>
/// Thin HTTP client for the gateway's <c>/api/analytics/*</c> endpoints. Returns the JSON body
/// verbatim — all analysis lives in the gateway; this process only adapts it to MCP tools.
/// Failures become <see cref="McpException"/> so the model sees an actionable message.
/// </summary>
public sealed class GatewayClient(HttpClient http, IOptions<GatewayOptions> options)
{
    public async Task<string> GetAsync(string path, IReadOnlyDictionary<string, object?>? query, CancellationToken ct)
    {
        var key = options.Value.ApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new McpException("The MCP server has no API key. Set KickGateway__ApiKey (same value as the gateway's Analytics__ApiKey).");

        using var request = new HttpRequestMessage(HttpMethod.Get, path + QueryString(query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"Could not reach the gateway at {http.BaseAddress}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException("The gateway did not answer in time — narrow the time window (from/to) or the channel.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode) return body;

            var status = (int)response.StatusCode;
            throw new McpException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    "The gateway rejected the API key (check KickGateway__ApiKey against the gateway's Analytics__ApiKey, min 24 chars).",
                HttpStatusCode.Forbidden => "The API key is not allowed to read this data.",
                _ when status is >= 300 and < 400 =>
                    "The gateway redirected to its login page — the API key header was not accepted (is Analytics__ApiKey set on the gateway?).",
                HttpStatusCode.BadRequest or HttpStatusCode.NotFound => ErrorText(body) ?? $"Gateway returned {status}.",
                _ => $"Gateway returned {status}: {Truncate(ErrorText(body) ?? body, 300)}",
            });
        }
    }

    private static string QueryString(IReadOnlyDictionary<string, object?>? query)
    {
        if (query is null) return "";
        var sb = new StringBuilder();
        foreach (var (name, value) in query)
        {
            var text = value switch
            {
                null => null,
                string s when string.IsNullOrWhiteSpace(s) => null,
                string s => s.Trim(),
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
            if (text is null) continue;
            sb.Append(sb.Length == 0 ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(text));
        }
        return sb.ToString();
    }

    private static string? ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
