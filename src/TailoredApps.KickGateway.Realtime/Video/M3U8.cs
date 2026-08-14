using System.Globalization;

namespace TailoredApps.KickGateway.Realtime.Video;

/// <summary>A variant stream from an HLS master playlist.</summary>
public record HlsVariant(Uri Uri, long Bandwidth, string? Resolution, string? Codecs);

/// <summary>A single media segment with its absolute sequence + discontinuity counter.</summary>
public record HlsSegment(Uri Uri, double Duration, long Sequence, int DiscontinuitySequence);

/// <summary>A parsed HLS media playlist (a live sliding window).</summary>
public record HlsMediaPlaylist(long MediaSequence, double TargetDuration, bool EndList, Uri? MapUri, IReadOnlyList<HlsSegment> Segments);

/// <summary>
/// Minimal HLS (m3u8) parser for the live capture loop — enough to select a master-playlist
/// variant and enumerate media segments with absolute sequence numbers, the fMP4 init segment
/// (<c>EXT-X-MAP</c>), and the end marker. Pure + side-effect free → unit tested.
/// </summary>
public static class M3U8
{
    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public static IReadOnlyList<HlsVariant> ParseMaster(string text, Uri baseUri)
    {
        var list = new List<HlsVariant>();
        var lines = SplitLines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) continue;
            var bandwidth = AttrLong(lines[i], "BANDWIDTH");
            var res = Attr(lines[i], "RESOLUTION");
            var codecs = Attr(lines[i], "CODECS");
            var uri = NextUri(lines, i);
            if (uri is not null && Uri.TryCreate(baseUri, uri, out var abs))
                list.Add(new HlsVariant(abs, bandwidth, res, codecs));
        }
        return list;
    }

    public static HlsMediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        long mediaSeq = 0;
        double target = 0;
        var endList = false;
        Uri? mapUri = null;
        var discSeq = 0;
        var segments = new List<HlsSegment>();

        var lines = SplitLines(text);
        long seq = 0;
        double pendingDur = 0;
        var haveInf = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '#')
            {
                if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                {
                    mediaSeq = ParseLong(line["#EXT-X-MEDIA-SEQUENCE:".Length..]);
                    seq = mediaSeq;
                }
                else if (line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:", StringComparison.Ordinal))
                {
                    discSeq = (int)ParseLong(line["#EXT-X-DISCONTINUITY-SEQUENCE:".Length..]);
                }
                else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                {
                    target = ParseDouble(line["#EXT-X-TARGETDURATION:".Length..]);
                }
                else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
                {
                    endList = true;
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    var u = Attr(line, "URI");
                    if (u is not null && Uri.TryCreate(baseUri, u, out var m)) mapUri = m;
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    pendingDur = ParseInf(line["#EXTINF:".Length..]);
                    haveInf = true;
                }
                else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal))
                {
                    discSeq++; // a plain #EXT-X-DISCONTINUITY (the :SEQUENCE variant matched above)
                }
                continue;
            }

            // A non-comment line is a media segment URI.
            if (Uri.TryCreate(baseUri, line, out var segUri))
                segments.Add(new HlsSegment(segUri, haveInf ? pendingDur : 0, seq, discSeq));
            seq++;
            haveInf = false;
            pendingDur = 0;
        }

        return new HlsMediaPlaylist(mediaSeq, target, endList, mapUri, segments);
    }

    /// <summary>Highest variant at or below the kbps cap; if none qualify, the lowest available. Null if empty.</summary>
    public static HlsVariant? ChooseVariant(IReadOnlyList<HlsVariant> variants, int maxBitrateKbps)
    {
        if (variants.Count == 0) return null;
        var cap = maxBitrateKbps <= 0 ? long.MaxValue : (long)maxBitrateKbps * 1000;
        HlsVariant? bestUnder = null;
        HlsVariant? lowest = null;
        foreach (var v in variants)
        {
            if (lowest is null || v.Bandwidth < lowest.Bandwidth) lowest = v;
            if (v.Bandwidth <= cap && (bestUnder is null || v.Bandwidth > bestUnder.Bandwidth)) bestUnder = v;
        }
        return bestUnder ?? lowest;
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static string? NextUri(string[] lines, int from)
    {
        for (var j = from + 1; j < lines.Length; j++)
        {
            var l = lines[j].Trim();
            if (l.Length == 0 || l[0] == '#') continue;
            return l;
        }
        return null;
    }

    private static string? Attr(string line, string key)
    {
        var from = 0;
        while (true)
        {
            var idx = line.IndexOf(key + "=", from, StringComparison.Ordinal);
            if (idx < 0) return null;
            var before = idx == 0 ? '\0' : line[idx - 1];
            if (before is ':' or ',' or '\0')
            {
                var start = idx + key.Length + 1;
                if (start >= line.Length) return "";
                if (line[start] == '"')
                {
                    var end = line.IndexOf('"', start + 1);
                    return end < 0 ? null : line[(start + 1)..end];
                }
                var comma = line.IndexOf(',', start);
                return comma < 0 ? line[start..] : line[start..comma];
            }
            from = idx + 1;
        }
    }

    private static long AttrLong(string line, string key) =>
        long.TryParse(Attr(line, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static long ParseLong(string s) =>
        long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static double ParseDouble(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static double ParseInf(string s)
    {
        var comma = s.IndexOf(',');
        var num = comma < 0 ? s : s[..comma];
        return ParseDouble(num);
    }
}
