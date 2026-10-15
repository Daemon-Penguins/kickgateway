using System.Globalization;

namespace TailoredApps.Integrations.Twitch.Playback;

/// <summary>A rendition from the usher master playlist (<c>EXT-X-MEDIA</c> + <c>EXT-X-STREAM-INF</c> pair).</summary>
public sealed record TwitchHlsVariant(string Name, Uri Uri, long Bandwidth, string? Codecs, string? Resolution, bool IsAudioOnly);

/// <summary>One media segment. <see cref="IsAd"/> = inside a stitched ad break (never forward it to a transcriber).</summary>
public sealed record TwitchHlsSegment(Uri Uri, double Duration, long Sequence, int DiscontinuitySequence, DateTime? ProgramDateTime, bool IsAd, string Title);

/// <summary>A stitched ad break as announced by an <c>EXT-X-DATERANGE</c> tag.</summary>
public sealed record TwitchHlsAdBreak(string Id, DateTime Start, DateTime? End);

public sealed record TwitchHlsMediaPlaylist(long MediaSequence, double TargetDuration, bool EndList, IReadOnlyList<TwitchHlsSegment> Segments, IReadOnlyList<TwitchHlsAdBreak> AdBreaks);

/// <summary>
/// Minimal parser for Twitch's HLS playlists: master (renditions incl. <c>audio_only</c>) and live media
/// playlists (per-segment <c>PROGRAM-DATE-TIME</c>, discontinuities, <c>EXT-X-TWITCH-PREFETCH</c> hints, and
/// the <c>EXT-X-DATERANGE CLASS="twitch-stitched-ad"</c> ad breaks Twitch splices in for non-subscribers).
/// Pure, so it is unit-tested against a captured playlist.
/// </summary>
public static class TwitchHls
{
    public const string AudioOnly = "audio_only";

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public static IReadOnlyList<TwitchHlsVariant> ParseMaster(string text, Uri baseUri)
    {
        var list = new List<TwitchHlsVariant>();
        var lines = SplitLines(text);
        string? pendingMedia = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                pendingMedia = line;
                continue;
            }
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;

            var bandwidth = AttrLong(line, "BANDWIDTH");
            var codecs = Attr(line, "CODECS");
            var resolution = Attr(line, "RESOLUTION");
            var video = Attr(line, "VIDEO") ?? "";
            var name = pendingMedia is not null ? Attr(pendingMedia, "NAME") : null;
            name = string.IsNullOrWhiteSpace(name) ? (video.Length > 0 ? video : $"{bandwidth}") : name;
            var isAudio = string.Equals(name, AudioOnly, StringComparison.OrdinalIgnoreCase)
                          || video.Equals(AudioOnly, StringComparison.OrdinalIgnoreCase)
                          || (resolution is null && codecs is not null && !codecs.Contains("avc", StringComparison.OrdinalIgnoreCase) && !codecs.Contains("hvc", StringComparison.OrdinalIgnoreCase) && !codecs.Contains("hev", StringComparison.OrdinalIgnoreCase) && !codecs.Contains("av01", StringComparison.OrdinalIgnoreCase));
            var uri = NextUri(lines, i);
            if (uri is not null && Uri.TryCreate(baseUri, uri, out var abs))
                list.Add(new TwitchHlsVariant(name, abs, bandwidth, codecs, resolution, isAudio));
            pendingMedia = null;
        }
        return list;
    }

    /// <summary>
    /// The rendition to capture: <paramref name="preferred"/> by name when present (<c>audio_only</c> by default),
    /// otherwise the highest video rendition at or below the kbps cap, otherwise the lowest. Null when the master is empty.
    /// </summary>
    public static TwitchHlsVariant? ChooseRendition(IReadOnlyList<TwitchHlsVariant> variants, string preferred, int maxBitrateKbps)
    {
        if (variants.Count == 0) return null;
        var byName = variants.FirstOrDefault(v => string.Equals(v.Name, preferred, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        if (string.Equals(preferred, AudioOnly, StringComparison.OrdinalIgnoreCase))
        {
            var audio = variants.FirstOrDefault(v => v.IsAudioOnly);
            if (audio is not null) return audio;
        }
        var cap = maxBitrateKbps <= 0 ? long.MaxValue : (long)maxBitrateKbps * 1000;
        TwitchHlsVariant? bestUnder = null, lowest = null;
        foreach (var v in variants.Where(v => !v.IsAudioOnly))
        {
            if (lowest is null || v.Bandwidth < lowest.Bandwidth) lowest = v;
            if (v.Bandwidth <= cap && (bestUnder is null || v.Bandwidth > bestUnder.Bandwidth)) bestUnder = v;
        }
        return bestUnder ?? lowest ?? variants[0];
    }

    public static TwitchHlsMediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        long mediaSeq = 0;
        double target = 0;
        var endList = false;
        var discSeq = 0;
        var segments = new List<TwitchHlsSegment>();
        var adBreaks = new List<TwitchHlsAdBreak>();

        var lines = SplitLines(text);
        long seq = 0;
        double pendingDur = 0;
        var pendingTitle = "";
        DateTime? pendingPdt = null;
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
                else if (line.StartsWith("#EXT-X-DATERANGE:", StringComparison.Ordinal))
                {
                    var cls = Attr(line, "CLASS") ?? "";
                    var id = Attr(line, "ID") ?? "";
                    if (cls == "twitch-stitched-ad" || id.StartsWith("stitched-ad-", StringComparison.Ordinal))
                    {
                        var start = ParseDate(Attr(line, "START-DATE"));
                        if (start is not null)
                        {
                            DateTime? end = ParseDate(Attr(line, "END-DATE"));
                            var duration = Attr(line, "DURATION") ?? Attr(line, "PLANNED-DURATION") ?? Attr(line, "X-TV-TWITCH-AD-POD-FILLED-DURATION");
                            if (end is null && duration is not null && double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                                end = start.Value.AddSeconds(d);
                            adBreaks.Add(new TwitchHlsAdBreak(id, start.Value, end));
                        }
                    }
                }
                else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal))
                {
                    pendingPdt = ParseDate(line["#EXT-X-PROGRAM-DATE-TIME:".Length..]);
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    var inf = line["#EXTINF:".Length..];
                    var comma = inf.IndexOf(',');
                    pendingDur = ParseDouble(comma < 0 ? inf : inf[..comma]);
                    pendingTitle = comma < 0 ? "" : inf[(comma + 1)..].Trim();
                    haveInf = true;
                }
                else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal))
                {
                    discSeq++;
                }
                // #EXT-X-TWITCH-PREFETCH: low-latency hints for segments that are not finished yet - ignored on purpose.
                continue;
            }

            if (Uri.TryCreate(baseUri, line, out var segUri))
            {
                var isAd = pendingTitle.Contains("Amazon", StringComparison.OrdinalIgnoreCase)
                           || (pendingPdt is { } pdt && adBreaks.Any(a => pdt >= a.Start && (a.End is null || pdt < a.End.Value)));
                segments.Add(new TwitchHlsSegment(segUri, haveInf ? pendingDur : 0, seq, discSeq, pendingPdt, isAd, pendingTitle));
            }
            seq++;
            haveInf = false;
            pendingDur = 0;
            pendingTitle = "";
            pendingPdt = null;
        }

        return new TwitchHlsMediaPlaylist(mediaSeq, target, endList, segments, adBreaks);
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

    /// <summary>Attribute value from an attribute-list tag (quoted values may contain commas).</summary>
    public static string? Attr(string line, string key)
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

    private static DateTime? ParseDate(string? s) =>
        s is not null && DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
}
