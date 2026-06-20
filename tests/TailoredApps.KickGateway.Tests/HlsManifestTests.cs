using TailoredApps.KickGateway.Api.Services;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class HlsManifestTests
{
    private const string ClipBase = "https://clips.kick.com/clips/71/clip_ABC/playlist.m3u8";
    private const string SegPath = "/api/obs/hls/clip_ABC/seg";

    [Fact]
    public void Rewrite_points_relative_segment_at_proxy_and_preserves_byterange()
    {
        var manifest = string.Join("\n",
            "#EXTM3U",
            "#EXT-X-VERSION:4",
            "#EXT-X-TARGETDURATION:2",
            "#EXT-X-BYTERANGE:2025700@5894928",
            "#EXTINF:2.000,",
            "71.ts",
            "#EXT-X-ENDLIST");

        var result = HlsManifest.Rewrite(manifest, new Uri(ClipBase), SegPath);

        // Tags preserved verbatim (byte-range is essential — proxy honours it via Range).
        Assert.Contains("#EXT-X-BYTERANGE:2025700@5894928", result);
        Assert.Contains("#EXTINF:2.000,", result);
        Assert.Contains("#EXT-X-ENDLIST", result);

        // The raw relative segment is replaced by a same-origin proxy URL.
        Assert.DoesNotContain("\n71.ts", result);
        Assert.Contains($"{SegPath}?u=", result);

        // ...and the token round-trips to the absolute CDN URL.
        var segLine = result.Split('\n').First(l => l.StartsWith(SegPath, StringComparison.Ordinal));
        var token = segLine[(segLine.IndexOf("u=", StringComparison.Ordinal) + 2)..];
        Assert.Equal("https://clips.kick.com/clips/71/clip_ABC/71.ts", HlsManifest.DecodeSegmentUrl(token));
    }

    [Fact]
    public void Rewrite_rewrites_uri_attribute_in_map_tag()
    {
        var manifest = string.Join("\n",
            "#EXTM3U",
            "#EXT-X-MAP:URI=\"init.mp4\"",
            "#EXTINF:4.0,",
            "seg0.m4s");

        var result = HlsManifest.Rewrite(manifest, new Uri(ClipBase), SegPath);

        Assert.Contains($"#EXT-X-MAP:URI=\"{SegPath}?u=", result);
        Assert.DoesNotContain("\"init.mp4\"", result);
    }

    [Fact]
    public void Rewrite_leaves_comment_lines_without_uri_untouched()
    {
        var manifest = "#EXTM3U\n#EXT-X-VERSION:4\n#EXT-X-PROGRAM-DATE-TIME:2026-06-18T22:03:57.106Z";
        var result = HlsManifest.Rewrite(manifest, new Uri(ClipBase), SegPath);
        Assert.Equal(manifest, result);
    }

    [Fact]
    public void DecodeSegmentUrl_returns_null_for_garbage()
    {
        Assert.Null(HlsManifest.DecodeSegmentUrl("@@@not-base64@@@"));
    }
}
