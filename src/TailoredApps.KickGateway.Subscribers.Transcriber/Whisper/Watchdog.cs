namespace TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;

/// <summary>Deadline wrapper for native calls that may never return.</summary>
public static class Watchdog
{
    /// <summary>
    /// Runs <paramref name="operation"/> with a deadline. Whisper.net honours the token between decoding
    /// steps, but a call stuck inside the GPU driver never returns — in that case the task is left
    /// running (nothing can abort it) and a <see cref="TimeoutException"/> is raised so recovery kicks in.
    /// Cancellation of <paramref name="ct"/> propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task WithDeadlineAsync(Func<CancellationToken, Task> operation, TimeSpan deadline, string what, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = operation(linked.Token);
        var winner = await Task.WhenAny(task, Task.Delay(deadline, ct));
        if (winner != task)
        {
            ct.ThrowIfCancellationRequested();
            linked.Cancel();
            throw new TimeoutException($"{what} did not finish within {deadline.TotalSeconds:F0}s — Whisper appears to be hung");
        }
        await task;
    }
}
