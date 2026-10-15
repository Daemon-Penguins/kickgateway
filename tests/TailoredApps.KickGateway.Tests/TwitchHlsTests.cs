using TailoredApps.Integrations.Twitch.Playback;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class TwitchHlsTests
{
    private static readonly Uri MasterUri = new("https://usher.ttvnw.net/api/channel/hls/somechannel.m3u8?sig=x&token=y");
    private static readonly Uri MediaUri = new("https://euc11.playlist.ttvnw.net/v1/playlist/abc.m3u8");

    // Shape of a real usher master (2026-10-15): an EXT-X-MEDIA + EXT-X-STREAM-INF pair per rendition, audio_only first.
    private const string Master = """
        #EXTM3U
        #EXT-X-TWITCH-INFO:NODE="cd1f0315f30a.j.cloudfront.hls.ttvnw.net",MANIFEST-NODE-TYPE="weaver_cluster",MANIFEST-NODE="elapsed"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio_only",NAME="audio_only",AUTOSELECT=NO,DEFAULT=NO
        #EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS="mp4a.40.2",VIDEO="audio_only"
        https://euc11.playlist.ttvnw.net/v1/playlist/audio.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="1080p60 (source)",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=6886390,RESOLUTION=1920x1080,CODECS="avc1.64002A,mp4a.40.2",VIDEO="chunked",FRAME-RATE=60.000
        https://euc11.playlist.ttvnw.net/v1/playlist/source.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="720p60",NAME="720p60",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=3422999,RESOLUTION=1280x720,CODECS="avc1.4D401F,mp4a.40.2",VIDEO="720p60",FRAME-RATE=60.000
        https://euc11.playlist.ttvnw.net/v1/playlist/720p60.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="160p30",NAME="160p",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=230000,RESOLUTION=284x160,CODECS="avc1.4D401F,mp4a.40.2",VIDEO="160p30",FRAME-RATE=30.000
        https://euc11.playlist.ttvnw.net/v1/playlist/160p.m3u8
        """;

    // Captured from a live audio_only playlist, trimmed, plus one stitched ad break injected at the end.
    private const string Media = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:5644
        #EXT-X-TWITCH-LIVE-SEQUENCE:5644
        #EXT-X-TWITCH-ELAPSED-SECS:11288.000
        #EXT-X-TWITCH-TOTAL-SECS:11320.000
        #EXT-X-DATERANGE:ID="playlist-creation-1792076995",CLASS="timestamp",START-DATE="2026-10-15T15:09:55.438Z",END-ON-NEXT=YES,X-SERVER-TIME="1792076995.44"
        #EXT-X-DATERANGE:ID="playlist-session-1792076995",CLASS="twitch-session",START-DATE="2026-10-15T15:09:55.438Z",END-ON-NEXT=YES,X-TV-TWITCH-SESSIONID="4927689358395280908"
        #EXT-X-DATERANGE:ID="source-1792076966",CLASS="twitch-stream-source",START-DATE="2026-10-15T15:09:26.131Z",END-ON-NEXT=YES,X-TV-TWITCH-STREAM-SOURCE="live"
        #EXT-X-DATERANGE:ID="stitched-ad-1792077000",CLASS="twitch-stitched-ad",START-DATE="2026-10-15T15:09:32.131Z",DURATION=4.000,X-TV-TWITCH-AD-ROLL-TYPE="MIDROLL",X-TV-TWITCH-AD-POD-FILLED-DURATION="4"
        #EXT-X-PROGRAM-DATE-TIME:2026-10-15T15:09:26.131Z
        #EXTINF:2.000,live
        https://cd1f0315f30a.j.cloudfront.hls.ttvnw.net/v1/segment/seg5644.ts
        #EXT-X-PROGRAM-DATE-TIME:2026-10-15T15:09:28.131Z
        #EXTINF:2.000,live
        https://cd1f0315f30a.j.cloudfront.hls.ttvnw.net/v1/segment/seg5645.ts
        #EXT-X-PROGRAM-DATE-TIME:2026-10-15T15:09:30.131Z
        #EXTINF:2.000,live
        https://cd1f0315f30a.j.cloudfront.hls.ttvnw.net/v1/segment/seg5646.ts
        #EXT-X-DISCONTINUITY
        #EXT-X-PROGRAM-DATE-TIME:2026-10-15T15:09:32.131Z
        #EXTINF:2.000,Amazon|123
        https://cd1f0315f30a.j.cloudfront.hls.ttvnw.net/v1/segment/ad1.ts
        #EXT-X-PROGRAM-DATE-TIME:2026-10-15T15:09:34.131Z
        #EXTINF:2.000,
        https://cd1f0315f30a.j.cloudfront.hls.ttvnw.net/v1/segment/ad2.ts
        #EXT-X-DISCONTINUITY
        #EXT-X-PROGRAM-DATE-TIME:2026-10-15T15:09:36.131Z
        #EXTINF:2.000,live
        https://cd1f0315f30a.j.cloudfront.hls.ttvnw.net/v1/segment/seg5649.ts
        #EXT-X-TWITCH-PREFETCH:https://cd1f0315f30a.j.cloudfront.hls.ttvnw.net/v1/segment/prefetch1.ts
        #EXT-X-TWITCH-PREFETCH:https://cd1f0315f30a.j.cloudfront.hls.ttvnw.net/v1/segment/prefetch2.ts
        """;

    [Fact]
    public void ParseMaster_reads_renditions_and_flags_audio_only()
    {
        Assert.True(TwitchHls.IsMaster(Master));
        var variants = TwitchHls.ParseMaster(Master, MasterUri);

        Assert.Equal(4, variants.Count);
        Assert.Equal("audio_only", variants[0].Name);
        Assert.True(variants[0].IsAudioOnly);
        Assert.Equal(160000, variants[0].Bandwidth);
        Assert.Equal("https://euc11.playlist.ttvnw.net/v1/playlist/audio.m3u8", variants[0].Uri.ToString());
        Assert.Equal("1080p60 (source)", variants[1].Name);
        Assert.False(variants[1].IsAudioOnly);
        Assert.Equal("1920x1080", variants[1].Resolution);
        Assert.Equal("160p", variants[3].Name);
    }

    [Fact]
    public void ChooseRendition_prefers_the_named_rendition_then_falls_back_by_bitrate()
    {
        var variants = TwitchHls.ParseMaster(Master, MasterUri);

        Assert.Equal("audio_only", TwitchHls.ChooseRendition(variants, "audio_only", 1500)!.Name);
        Assert.Equal("720p60", TwitchHls.ChooseRendition(variants, "720p60", 1500)!.Name);
        // Unknown name -> highest VIDEO rendition under the cap (audio is never picked by accident).
        Assert.Equal("720p60", TwitchHls.ChooseRendition(variants, "480p", 3500)!.Name);
        Assert.Equal("160p", TwitchHls.ChooseRendition(variants, "480p", 100)!.Name);
        Assert.Null(TwitchHls.ChooseRendition([], "audio_only", 1500));
    }

    [Fact]
    public void ParseMedia_reads_sequence_program_date_time_and_ignores_prefetch()
    {
        var playlist = TwitchHls.ParseMedia(Media, MediaUri);

        Assert.Equal(5644, playlist.MediaSequence);
        Assert.Equal(6, playlist.TargetDuration);
        Assert.False(playlist.EndList);
        Assert.Equal(6, playlist.Segments.Count); // prefetch hints are not segments
        Assert.Equal(5644, playlist.Segments[0].Sequence);
        Assert.Equal(new DateTime(2026, 10, 15, 15, 9, 26, 131, DateTimeKind.Utc), playlist.Segments[0].ProgramDateTime);
        Assert.Equal(2.0, playlist.Segments[0].Duration);
        Assert.Equal("live", playlist.Segments[0].Title);
        Assert.Equal(5649, playlist.Segments[5].Sequence);
        Assert.DoesNotContain(playlist.Segments, s => s.Uri.ToString().Contains("prefetch"));
    }

    [Fact]
    public void ParseMedia_marks_stitched_ad_segments_and_counts_discontinuities()
    {
        var playlist = TwitchHls.ParseMedia(Media, MediaUri);

        var ad = Assert.Single(playlist.AdBreaks);
        Assert.Equal("stitched-ad-1792077000", ad.Id);
        Assert.Equal(new DateTime(2026, 10, 15, 15, 9, 36, 131, DateTimeKind.Utc), ad.End);

        Assert.False(playlist.Segments[2].IsAd);
        Assert.True(playlist.Segments[3].IsAd);  // "Amazon" title + inside the daterange
        Assert.True(playlist.Segments[4].IsAd);  // inside the daterange only
        Assert.False(playlist.Segments[5].IsAd); // daterange ended
        Assert.Equal(0, playlist.Segments[2].DiscontinuitySequence);
        Assert.Equal(1, playlist.Segments[3].DiscontinuitySequence);
        Assert.Equal(2, playlist.Segments[5].DiscontinuitySequence);
    }

    [Fact]
    public void Attr_reads_quoted_values_with_commas()
    {
        const string line = "#EXT-X-DATERANGE:ID=\"stitched-ad-1\",CLASS=\"twitch-stitched-ad\",START-DATE=\"2026-10-15T15:09:32.131Z\",DURATION=4.000,X-TV-TWITCH-AD-URL=\"https://x/y?a=1,b=2\"";
        Assert.Equal("stitched-ad-1", TwitchHls.Attr(line, "ID"));
        Assert.Equal("twitch-stitched-ad", TwitchHls.Attr(line, "CLASS"));
        Assert.Equal("4.000", TwitchHls.Attr(line, "DURATION"));
        Assert.Equal("https://x/y?a=1,b=2", TwitchHls.Attr(line, "X-TV-TWITCH-AD-URL"));
        Assert.Null(TwitchHls.Attr(line, "END-DATE"));
    }
}
