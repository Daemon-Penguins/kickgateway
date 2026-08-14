using TailoredApps.KickGateway.Realtime.Video;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class HlsCaptureParserTests
{
    private static readonly Uri MasterUri = new("https://stream.kick.com/live/master.m3u8");
    private static readonly Uri MediaUri = new("https://stream.kick.com/live/720p/index.m3u8");

    private const string Master = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
        360p/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=3500000,RESOLUTION=1280x720
        720p/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1080
        1080p/index.m3u8
        """;

    [Fact]
    public void ParseMaster_reads_variants_and_resolves_uris()
    {
        Assert.True(M3U8.IsMaster(Master));
        var variants = M3U8.ParseMaster(Master, MasterUri);

        Assert.Equal(3, variants.Count);
        Assert.Equal(1280000, variants[0].Bandwidth);
        Assert.Equal("640x360", variants[0].Resolution);
        Assert.Equal("https://stream.kick.com/live/360p/index.m3u8", variants[0].Uri.ToString());
        Assert.Equal(6000000, variants[2].Bandwidth);
    }

    [Fact]
    public void ChooseVariant_picks_highest_at_or_below_cap()
    {
        var variants = M3U8.ParseMaster(Master, MasterUri);
        Assert.Equal(3500000, M3U8.ChooseVariant(variants, 3500)!.Bandwidth); // 3.5 Mbps cap → 720p
    }

    [Fact]
    public void ChooseVariant_falls_back_to_lowest_when_all_exceed_cap()
    {
        var variants = M3U8.ParseMaster(Master, MasterUri);
        Assert.Equal(1280000, M3U8.ChooseVariant(variants, 500)!.Bandwidth);
    }

    [Fact]
    public void ChooseVariant_zero_cap_picks_highest()
    {
        var variants = M3U8.ParseMaster(Master, MasterUri);
        Assert.Equal(6000000, M3U8.ChooseVariant(variants, 0)!.Bandwidth);
    }

    [Fact]
    public void ParseMedia_reads_sequence_map_and_segments()
    {
        const string media = """
            #EXTM3U
            #EXT-X-VERSION:6
            #EXT-X-TARGETDURATION:4
            #EXT-X-MEDIA-SEQUENCE:100
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:4.000,
            seg100.m4s
            #EXTINF:4.000,
            seg101.m4s
            #EXT-X-DISCONTINUITY
            #EXTINF:2.000,
            seg102.m4s
            """;

        var pl = M3U8.ParseMedia(media, MediaUri);

        Assert.Equal(100, pl.MediaSequence);
        Assert.Equal(4, pl.TargetDuration);
        Assert.False(pl.EndList);
        Assert.Equal("https://stream.kick.com/live/720p/init.mp4", pl.MapUri!.ToString());

        Assert.Equal(3, pl.Segments.Count);
        Assert.Equal(100, pl.Segments[0].Sequence);
        Assert.Equal("https://stream.kick.com/live/720p/seg100.m4s", pl.Segments[0].Uri.ToString());
        Assert.Equal(4.0, pl.Segments[0].Duration);
        Assert.Equal(0, pl.Segments[0].DiscontinuitySequence);

        Assert.Equal(101, pl.Segments[1].Sequence);

        // After #EXT-X-DISCONTINUITY the counter increments.
        Assert.Equal(102, pl.Segments[2].Sequence);
        Assert.Equal(1, pl.Segments[2].DiscontinuitySequence);
        Assert.Equal(2.0, pl.Segments[2].Duration);
    }

    [Fact]
    public void ParseMedia_detects_endlist()
    {
        const string vod = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:0
            #EXTINF:6.0,
            0.ts
            #EXT-X-ENDLIST
            """;

        var pl = M3U8.ParseMedia(vod, MediaUri);
        Assert.True(pl.EndList);
        Assert.Single(pl.Segments);
        Assert.Equal(0, pl.Segments[0].Sequence);
        Assert.Equal("https://stream.kick.com/live/720p/0.ts", pl.Segments[0].Uri.ToString());
    }
}
