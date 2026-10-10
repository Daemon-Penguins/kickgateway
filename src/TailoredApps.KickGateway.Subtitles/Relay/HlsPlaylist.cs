using System.Globalization;
using System.Text;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subtitles.Relay;

/// <summary>One buffered media (or init) segment of a channel, renumbered for our own playlist.</summary>
/// <param name="LocalSequence">Our contiguous media sequence (Kick's numbering can have holes when the capture drops a segment).</param>
/// <param name="DiscontinuityIndex">Our running discontinuity count at this segment; a jump between neighbours emits <c>#EXT-X-DISCONTINUITY</c>.</param>
/// <param name="ContentStartUtc">When the segment's content started on the gateway's clock (<c>CapturedAt − Duration</c>) — the same timeline the transcriber stamps transcripts with, so captions align on <c>#EXT-X-PROGRAM-DATE-TIME</c>.</param>
public sealed record BufferedSegment(
    long LocalSequence,
    long MediaSequence,
    int DiscontinuityIndex,
    double Duration,
    DateTime ContentStartUtc,
    DateTime ReceivedAt,
    MediaContainer Container,
    byte[] Data)
{
    public string Extension => Container == MediaContainer.Fmp4 ? "m4s" : "ts";
    public string ContentType => Container == MediaContainer.Fmp4 ? "video/iso.segment" : "video/mp2t";
}

/// <summary>Renders a live media playlist for a channel's buffer. Pure.</summary>
public static class HlsPlaylist
{
    /// <param name="querySuffix">Appended to every URI (token + viewer id) — hls.js fetches segment URIs verbatim, relative to the playlist.</param>
    public static string Build(IReadOnlyList<BufferedSegment> segments, bool hasInit, string querySuffix)
    {
        if (segments.Count == 0) throw new ArgumentException("A playlist needs at least one segment.", nameof(segments));

        var sb = new StringBuilder(segments.Count * 120 + 200);
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:6\n");
        sb.Append("#EXT-X-TARGETDURATION:").Append(Math.Max(1, (int)Math.Ceiling(segments.Max(s => s.Duration)))).Append('\n');
        sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(segments[0].LocalSequence).Append('\n');
        sb.Append("#EXT-X-DISCONTINUITY-SEQUENCE:").Append(segments[0].DiscontinuityIndex).Append('\n');
        if (hasInit)
            sb.Append("#EXT-X-MAP:URI=\"init.mp4").Append(querySuffix).Append("\"\n");

        var previousDiscontinuity = segments[0].DiscontinuityIndex;
        foreach (var s in segments)
        {
            if (s.DiscontinuityIndex != previousDiscontinuity)
            {
                sb.Append("#EXT-X-DISCONTINUITY\n");
                previousDiscontinuity = s.DiscontinuityIndex;
            }
            sb.Append("#EXT-X-PROGRAM-DATE-TIME:").Append(s.ContentStartUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#EXTINF:").Append(s.Duration.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("seg/").Append(s.LocalSequence).Append('.').Append(s.Extension).Append(querySuffix).Append('\n');
        }
        return sb.ToString();
    }
}
