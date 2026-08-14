using TailoredApps.KickGateway.Realtime;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class RealtimeHttpClientsTests
{
    // Kick's live playback URL is served by Amazon IVS, NOT kick.com. The capture loop must
    // accept it (this is the bug that kept LiveVideoSegment from ever publishing).
    private const string IvsMaster =
        "https://fa723fc1b171.us-west-2.playback.live-video.net/api/video/v1/us-west-2.196233775518.channel.abc.m3u8";

    [Theory]
    [InlineData(IvsMaster, true)]
    [InlineData("https://stream.kick.com/live/master.m3u8", true)]
    [InlineData("https://segments.some-other-cdn.net/seg100.ts", true)] // cross-host segments are allowed (https only)
    [InlineData("http://insecure.example/master.m3u8", false)]          // must be https
    [InlineData("not-a-url", false)]
    public void IsFetchableHttps_requires_absolute_https(string url, bool expected)
        => Assert.Equal(expected, RealtimeHttpClients.IsFetchableHttps(url));
}
