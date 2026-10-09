using TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class WatchdogTests
{
    [Fact]
    public async Task Completing_operation_passes_through()
    {
        var ran = false;
        await Watchdog.WithDeadlineAsync(_ => { ran = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(5), "op", default);
        Assert.True(ran);
    }

    [Fact]
    public async Task Operation_exception_propagates_unchanged()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Watchdog.WithDeadlineAsync(_ => Task.FromException(new InvalidOperationException("boom")), TimeSpan.FromSeconds(5), "op", default));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Hung_operation_times_out_and_gets_cancelled()
    {
        var hung = new TaskCompletionSource();
        CancellationToken seen = default;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            Watchdog.WithDeadlineAsync(token => { seen = token; return hung.Task; }, TimeSpan.FromMilliseconds(100), "transcribing", default));

        Assert.Contains("transcribing", ex.Message);
        Assert.True(seen.IsCancellationRequested, "the operation's token should be cancelled so a cooperative callee can stop");
        Assert.False(hung.Task.IsCompleted, "a truly hung task is abandoned, not awaited");
    }

    [Fact]
    public async Task Host_cancellation_wins_over_the_deadline()
    {
        using var cts = new CancellationTokenSource();
        var hung = new TaskCompletionSource();
        var run = Watchdog.WithDeadlineAsync(_ => hung.Task, TimeSpan.FromMinutes(1), "op", cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
