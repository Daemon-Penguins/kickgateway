using System.Text;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class LiveVideoSegmentContainerTests
{
    private static byte[] TsBytes(int packets = 3)
    {
        var buf = new byte[188 * packets];
        for (var i = 0; i < packets; i++) buf[188 * i] = 0x47;
        return buf;
    }

    private static byte[] BoxBytes(string type)
    {
        var buf = new byte[16];
        buf[3] = 16; // size
        Encoding.ASCII.GetBytes(type).CopyTo(buf, 4);
        return buf;
    }

    [Fact]
    public void Kick_cdn_ts_segment_with_octet_stream_mime_and_query_string_url_is_ts()
    {
        // Exactly what the production CDN sends: TS bytes, generic MIME, ".ts?dna=..." URL.
        var seg = new LiveVideoSegment
        {
            ContentType = "application/octet-stream",
            SegmentUri = "https://x.cloudfront.hls.live-video.net/v1/segment/Cv…pBA.ts?dna=CmYl06a1",
            Data = TsBytes(),
        };

        Assert.Equal(MediaContainer.TransportStream, seg.DetectContainer());
        Assert.True(seg.IsTransportStream());
    }

    [Fact]
    public void Query_string_url_without_sniffable_bytes_still_resolves_by_path_extension()
    {
        var ts = new LiveVideoSegment { ContentType = "application/octet-stream", SegmentUri = "https://cdn/seg.ts?dna=abc", Data = [1, 2, 3] };
        var m4s = new LiveVideoSegment { ContentType = "application/octet-stream", SegmentUri = "https://cdn/seg.m4s?dna=abc", Data = [1, 2, 3] };

        Assert.Equal(MediaContainer.TransportStream, ts.DetectContainer());
        Assert.Equal(MediaContainer.Fmp4, m4s.DetectContainer());
    }

    [Theory]
    [InlineData("styp")]
    [InlineData("moof")]
    [InlineData("sidx")]
    [InlineData("ftyp")]
    public void Iso_bmff_box_header_is_fmp4_even_when_metadata_says_otherwise(string box)
    {
        var seg = new LiveVideoSegment { ContentType = "video/mp2t", SegmentUri = "https://cdn/seg.ts", Data = BoxBytes(box) };
        Assert.Equal(MediaContainer.Fmp4, seg.DetectContainer());
        Assert.False(seg.IsTransportStream());
    }

    [Fact]
    public void Sniffed_bytes_beat_mime_type()
    {
        var seg = new LiveVideoSegment { ContentType = "video/mp4", SegmentUri = "https://cdn/seg.m4s", Data = TsBytes() };
        Assert.Equal(MediaContainer.TransportStream, seg.DetectContainer());
    }

    [Fact]
    public void Mime_type_is_used_when_bytes_are_inconclusive()
    {
        Assert.Equal(MediaContainer.TransportStream,
            new LiveVideoSegment { ContentType = "video/MP2T", SegmentUri = "https://cdn/x", Data = [1, 2, 3] }.DetectContainer());
        Assert.Equal(MediaContainer.Fmp4,
            new LiveVideoSegment { ContentType = "video/mp4", SegmentUri = "https://cdn/x", Data = [1, 2, 3] }.DetectContainer());
    }

    [Fact]
    public void Unknown_when_nothing_identifies_the_container_and_that_counts_as_ts()
    {
        var seg = new LiveVideoSegment { ContentType = "application/octet-stream", SegmentUri = "https://cdn/x", Data = [1, 2, 3] };
        Assert.Equal(MediaContainer.Unknown, seg.DetectContainer());
        Assert.True(seg.IsTransportStream()); // self-describing TS is the safe default — ffmpeg probes it anyway
    }

    [Fact]
    public void Ts_sniff_needs_sync_bytes_at_three_packet_boundaries()
    {
        var almost = TsBytes();
        almost[188] = 0x00; // break the second sync byte
        Assert.Equal(MediaContainer.Unknown, LiveVideoSegmentExtensions.SniffContainer(almost));
        Assert.Equal(MediaContainer.Unknown, LiveVideoSegmentExtensions.SniffContainer(TsBytes(2))); // too short
    }
}
