using TailoredApps.KickGateway.Realtime.Pusher;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class PusherReconnectTests
{
    [Fact]
    public void TryParseError_reads_code_and_message()
    {
        var ok = PusherConnection.TryParseError(
            "{\"code\":4001,\"message\":\"App key 32cbd69e4b950bf97679 not in this cluster. Did you forget to specify the cluster?\"}",
            out var code, out var message);

        Assert.True(ok);
        Assert.Equal(4001, code);
        Assert.StartsWith("App key 32cbd69e4b950bf97679 not in this cluster", message);
        Assert.True(new PusherErrorException(code, message).Refused);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"message\":\"no code\"}")]
    [InlineData("{\"code\":\"4001\"}")]
    public void TryParseError_is_lenient_about_other_shapes(string data)
    {
        var ok = PusherConnection.TryParseError(data, out var code, out var message);

        Assert.False(ok);
        Assert.Equal(0, code);
        Assert.NotNull(message);
        Assert.False(new PusherErrorException(code, message).Refused);
    }

    [Theory]
    [InlineData(4000, true)]
    [InlineData(4001, true)]
    [InlineData(4099, true)]
    [InlineData(4100, false)] // "try again in a bit" - a normal reconnect
    [InlineData(4200, false)]
    [InlineData(0, false)]
    public void Refused_covers_only_the_do_not_retry_range(int code, bool refused)
        => Assert.Equal(refused, new PusherErrorException(code, "x").Refused);

    [Fact]
    public void NextBackoff_waits_the_maximum_after_a_refusal()
        => Assert.Equal(30, PusherConnection.NextBackoff(previous: 1, established: false, refused: true, min: 1, max: 30));

    [Fact]
    public void NextBackoff_resets_only_after_an_established_session()
    {
        // The handshake alone must not reset it: a refused key completes the handshake too.
        Assert.Equal(1, PusherConnection.NextBackoff(previous: 16, established: true, refused: false, min: 1, max: 30));
        Assert.Equal(30, PusherConnection.NextBackoff(previous: 16, established: false, refused: false, min: 1, max: 30));
    }

    [Fact]
    public void NextBackoff_doubles_while_sessions_fail_before_being_established()
    {
        var waits = new List<int>();
        var backoff = 1;
        for (var i = 0; i < 7; i++) waits.Add(backoff = PusherConnection.NextBackoff(backoff, established: false, refused: false, min: 1, max: 30));

        Assert.Equal([2, 4, 8, 16, 30, 30, 30], waits);
    }

    [Fact]
    public void NextBackoff_clamps_bad_options()
    {
        Assert.Equal(1, PusherConnection.NextBackoff(previous: 0, established: true, refused: false, min: 0, max: 0));
        Assert.Equal(1, PusherConnection.NextBackoff(previous: 0, established: false, refused: false, min: 0, max: 0)); // min and max clamp to 1
        Assert.Equal(5, PusherConnection.NextBackoff(previous: 99, established: false, refused: true, min: 5, max: 2)); // max below min -> min
    }
}
