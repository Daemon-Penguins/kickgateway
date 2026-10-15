using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using TailoredApps.KickGateway.Realtime;
using TailoredApps.KickGateway.Realtime.Ingest;
using TailoredApps.KickGateway.Realtime.Pusher;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

// Shares the process-wide KickGatewayMetrics state (queue-depth provider) with KickGatewayMetricsTests.
[Collection("KickGatewayMetrics")]
public class RealtimeIngestQueueTests
{
    private static readonly ManagedChannel Meta = new("xqc", "668", "12345");

    private static PusherFrame Frame(string channel, int n) =>
        new("App\\Events\\ChatMessageEvent", channel, $"{{\"n\":{n}}}");

    [Fact]
    public async Task Keeps_frames_of_one_channel_in_order()
    {
        var seen = new ConcurrentDictionary<string, List<string>>();
        var queue = new RealtimeIngestQueue(4, 100, 0, TimeSpan.Zero, async (frame, _, _, ct) =>
        {
            await Task.Delay(Random.Shared.Next(0, 3), ct);
            // One channel always lands on one worker, so this list is only ever touched by one thread.
            seen.GetOrAdd(frame.Channel!, _ => new List<string>()).Add(frame.Data);
            return IngestOutcome.Published;
        }, NullLogger.Instance);
        queue.Start();

        string[] channels = ["chatrooms.1.v2", "chatrooms.2.v2", "channel.3"];
        for (var i = 0; i < 40; i++)
            foreach (var ch in channels)
                Assert.True(queue.TryEnqueue(Frame(ch, i), Meta, DateTime.UtcNow));

        await queue.DisposeAsync(); // drains

        Assert.Equal(channels.Length, seen.Count);
        foreach (var ch in channels)
            Assert.Equal(Enumerable.Range(0, 40).Select(i => $"{{\"n\":{i}}}"), seen[ch]);
        Assert.Equal(0, queue.Dropped);
    }

    [Fact]
    public async Task A_stalled_channel_does_not_block_another_partition()
    {
        const string a = "chatrooms.1.v2";
        var release = new TaskCompletionSource();
        var bDone = new TaskCompletionSource();
        var queue = new RealtimeIngestQueue(4, 100, 0, TimeSpan.Zero, async (frame, _, _, ct) =>
        {
            if (frame.Channel == a) await release.Task.WaitAsync(ct);
            else bDone.TrySetResult();
            return IngestOutcome.Published;
        }, NullLogger.Instance);
        var b = Enumerable.Range(2, 200).Select(i => $"chatrooms.{i}.v2").First(c => queue.PartitionOf(c) != queue.PartitionOf(a));
        queue.Start();

        Assert.True(queue.TryEnqueue(Frame(a, 0), Meta, DateTime.UtcNow));
        Assert.True(queue.TryEnqueue(Frame(b, 0), Meta, DateTime.UtcNow));

        await bDone.Task.WaitAsync(TimeSpan.FromSeconds(5)); // b committed while a is still stuck
        release.SetResult();
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task Drops_the_newest_frame_when_a_partition_is_full_and_counts_it()
    {
        // Workers not started: the partition fills up deterministically.
        var queue = new RealtimeIngestQueue(1, 2, 0, TimeSpan.Zero,
            (_, _, _, _) => Task.FromResult(IngestOutcome.Published), NullLogger.Instance);

        Assert.True(queue.TryEnqueue(Frame("chatrooms.1.v2", 0), Meta, DateTime.UtcNow));
        Assert.True(queue.TryEnqueue(Frame("chatrooms.1.v2", 1), Meta, DateTime.UtcNow));
        Assert.False(queue.TryEnqueue(Frame("chatrooms.1.v2", 2), Meta, DateTime.UtcNow));

        Assert.Equal(1, queue.Dropped);
        Assert.Equal(2, queue.Depth);
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task Retries_a_failing_frame_then_gives_up()
    {
        var attempts = 0;
        var queue = new RealtimeIngestQueue(1, 10, retries: 2, TimeSpan.FromMilliseconds(1), (_, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("db down");
        }, NullLogger.Instance);
        queue.Start();

        Assert.True(queue.TryEnqueue(Frame("chatrooms.1.v2", 0), Meta, DateTime.UtcNow));
        await queue.DisposeAsync();

        Assert.Equal(3, attempts); // 1 + 2 retries
    }

    [Fact]
    public async Task Dispose_drains_what_was_queued_before_start()
    {
        var n = 0;
        var queue = new RealtimeIngestQueue(2, 100, 0, TimeSpan.Zero, (_, _, _, _) =>
        {
            Interlocked.Increment(ref n);
            return Task.FromResult(IngestOutcome.Published);
        }, NullLogger.Instance);

        for (var i = 0; i < 20; i++)
            Assert.True(queue.TryEnqueue(Frame($"chatrooms.{i}.v2", i), Meta, DateTime.UtcNow));
        queue.Start();
        await queue.DisposeAsync();

        Assert.Equal(20, n);
        Assert.Equal(0, queue.Depth);
    }
}
