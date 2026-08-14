using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.VideoRecorder;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class VideoRecorderTests
{
    private static LiveVideoSegment Seg(string contentType, string uri, bool init = false) => new()
    {
        BroadcasterSlug = "xqc",
        ContentType = contentType,
        SegmentUri = uri,
        IsInitSegment = init,
        Data = new byte[] { 1, 2, 3 },
    };

    [Fact]
    public void TransportStream_detected_by_mime()
        => Assert.True(VideoFileAssembler.IsTransportStream(Seg("video/mp2t", "https://cdn/seg1.ts")));

    [Fact]
    public void Fmp4_not_transport_stream_by_mime()
        => Assert.False(VideoFileAssembler.IsTransportStream(Seg("video/mp4", "https://cdn/seg1.m4s")));

    [Theory]
    [InlineData("https://cdn/segment0.ts", true)]   // .ts extension → TS
    [InlineData("https://cdn/segment0.m4s", false)] // .m4s → fMP4
    public void Container_falls_back_to_url_extension_when_mime_ambiguous(string uri, bool expectedTs)
        => Assert.Equal(expectedTs, VideoFileAssembler.IsTransportStream(Seg("application/octet-stream", uri)));
}
