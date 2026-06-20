using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace TailoredApps.KickGateway.Api.Services;

/// <summary>
/// Rewrites Kick clip HLS playlists so the browser/OBS fetches every segment from
/// our same-origin segment proxy instead of the clips CDN directly (the CDN sends
/// no CORS header). Pure + side-effect free → unit-testable.
/// </summary>
public static class HlsManifest
{
    /// <summary>
    /// Rewrites a media playlist: each segment URI and each <c>URI="…"</c> attribute
    /// (EXT-X-MAP / EXT-X-KEY / …) is resolved against <paramref name="manifestUri"/>
    /// and pointed at <paramref name="segProxyPath"/>?u=&lt;token&gt;. All other lines —
    /// including EXT-X-BYTERANGE, which the proxy honours via forwarded Range headers —
    /// are preserved verbatim.
    /// </summary>
    public static string Rewrite(string manifest, Uri manifestUri, string segProxyPath)
    {
        var sb = new StringBuilder(manifest.Length + 256);
        var lines = manifest.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            var line = lines[i];

            if (line.Length == 0)
                continue;
            if (line[0] == '#')
            {
                sb.Append(RewriteUriAttribute(line, manifestUri, segProxyPath));
                continue;
            }
            sb.Append(ToProxyUrl(line.Trim(), manifestUri, segProxyPath));
        }
        return sb.ToString();
    }

    /// <summary>Decodes the base64url segment token produced by <see cref="ToProxyUrl"/>. Null if malformed.</summary>
    public static string? DecodeSegmentUrl(string token)
    {
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)); }
        catch { return null; }
    }

    private static string RewriteUriAttribute(string line, Uri baseUri, string segProxyPath)
    {
        const string key = "URI=\"";
        var idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return line;
        var start = idx + key.Length;
        var end = line.IndexOf('"', start);
        if (end < 0) return line;

        var uri = line[start..end];
        var proxied = ToProxyUrl(uri, baseUri, segProxyPath);
        return string.Concat(line.AsSpan(0, start), proxied, line.AsSpan(end));
    }

    private static string ToProxyUrl(string uri, Uri baseUri, string segProxyPath)
    {
        if (string.IsNullOrWhiteSpace(uri)) return uri;
        var abs = Uri.TryCreate(baseUri, uri, out var resolved) ? resolved.ToString() : uri;
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(abs));
        return $"{segProxyPath}?u={token}";
    }
}
