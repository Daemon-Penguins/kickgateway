using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace TailoredApps.KickGateway.Mcp.OAuth;

/// <summary>What the authorization server fetches on a client's behalf: its metadata document and its JWK Set.</summary>
public interface IClientMetadataDocuments
{
    /// <summary>Resolves a Client ID Metadata Document (an https client id) into a client. Null = unknown/invalid.</summary>
    Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct);

    /// <summary>The client's JWK Set for <c>private_key_jwt</c>. <paramref name="refresh"/> bypasses the cache (unknown kid = key rotation).</summary>
    Task<JsonObject?> GetJwksAsync(string jwksUri, bool refresh, CancellationToken ct);
}

/// <summary>
/// Fetches over HTTPS (no redirects, 5 s, 64 KB cap) and caches the verdict — an hour for a valid document, a
/// minute for a failure — so a client id is fetched once per authorization, not once per token request.
/// </summary>
public sealed class HttpClientMetadataDocuments(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<HttpClientMetadataDocuments> log) : IClientMetadataDocuments
{
    public const string HttpClientName = "client-metadata-documents";
    private const int MaxBytes = 64 * 1024;
    private static readonly TimeSpan ValidTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan InvalidTtl = TimeSpan.FromMinutes(1);

    private sealed record ClientEntry(OAuthClient? Client);

    private sealed record JwksEntry(JsonObject? Jwks);

    public async Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct)
    {
        if (!OAuthClient.IsMetadataDocumentUrl(clientId, out var uri)) return null;

        var cacheKey = "cimd:" + clientId;
        if (cache.TryGetValue(cacheKey, out ClientEntry? cached) && cached is not null) return cached.Client;

        OAuthClient? client = null;
        var document = await FetchJsonAsync(uri, ct);
        if (document is not null)
        {
            client = OAuthClient.FromMetadataDocument(clientId, document, out var problem);
            if (client is null) log.LogWarning("Client metadata document {Url} rejected: {Problem}", clientId, problem);
        }
        cache.Set(cacheKey, new ClientEntry(client), client is null ? InvalidTtl : ValidTtl);
        return client;
    }

    public async Task<JsonObject?> GetJwksAsync(string jwksUri, bool refresh, CancellationToken ct)
    {
        if (!OAuthClient.IsFetchableHttpsUrl(jwksUri, out var uri)) return null;

        var cacheKey = "jwks:" + jwksUri;
        if (refresh) cache.Remove(cacheKey);
        else if (cache.TryGetValue(cacheKey, out JwksEntry? cached) && cached is not null) return cached.Jwks;

        var jwks = await FetchJsonAsync(uri, ct);
        if (jwks is not null && jwks["keys"] is not JsonArray)
        {
            log.LogWarning("JWKS {Url} rejected: no 'keys' array", jwksUri);
            jwks = null;
        }
        cache.Set(cacheKey, new JwksEntry(jwks), jwks is null ? InvalidTtl : ValidTtl);
        return jwks;
    }

    private async Task<JsonObject?> FetchJsonAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var http = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > MaxBytes)
            {
                log.LogWarning("{Url} rejected: HTTP {Status}, {Length} bytes", uri, (int)response.StatusCode, response.Content.Headers.ContentLength);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxBytes + 1];
            int total = 0, read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0) total += read;
            if (total > MaxBytes)
            {
                log.LogWarning("{Url} rejected: larger than {Max} bytes", uri, MaxBytes);
                return null;
            }

            if (JsonNode.Parse(buffer.AsSpan(0, total)) is JsonObject document) return document;
            log.LogWarning("{Url} rejected: not a JSON object", uri);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            log.LogWarning("{Url} could not be fetched: {Error}", uri, ex.Message);
            return null;
        }
    }
}
