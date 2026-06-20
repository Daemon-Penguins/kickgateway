using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TailoredApps.Integrations.Kick.Sidecar;

public class KickSidecarFetcher : IKickSidecarFetcher
{
    /// <summary>Named HttpClient that talks to the clips-fetcher sidecar.</summary>
    public const string HttpClientName = "KickClipsFetcher";

    private readonly IHttpClientFactory _factory;
    private readonly KickGlobalDefaults _defaults;
    private readonly ILogger<KickSidecarFetcher> _log;

    public KickSidecarFetcher(IHttpClientFactory factory, IOptions<KickGlobalDefaults> defaults, ILogger<KickSidecarFetcher> log)
    {
        _factory = factory;
        _defaults = defaults.Value;
        _log = log;
    }

    public async Task<string?> FetchAsync(string kickUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_defaults.ClipsFetcherUrl))
        {
            _log.LogError("Kick sidecar not configured (Kick:ClipsFetcherUrl is empty) — cannot fetch {Url}", kickUrl);
            return null;
        }

        var fetchUrl = $"{_defaults.ClipsFetcherUrl.TrimEnd('/')}/fetch?url={Uri.EscapeDataString(kickUrl)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
        if (!string.IsNullOrEmpty(_defaults.ClipsFetcherSecret))
            req.Headers.TryAddWithoutValidation("X-Fetch-Secret", _defaults.ClipsFetcherSecret);

        try
        {
            var http = _factory.CreateClient(HttpClientName);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Sidecar fetch failed ({Status}) for {Url}: {Body}",
                    (int)resp.StatusCode, kickUrl, Truncate(body, 300));
                return null;
            }
            return body;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sidecar fetch threw for {Url}", kickUrl);
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
