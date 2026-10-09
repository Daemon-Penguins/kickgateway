using TailoredApps.KickGateway.Subscribers.Transcriber.Audio;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class SegmentTimelineTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>Three back-to-back 4 s segments, each "captured" right as it ended.</summary>
    private static SegmentTimeline Three()
    {
        var tl = new SegmentTimeline();
        tl.Add(100, 4, T0.AddSeconds(4));
        tl.Add(101, 4, T0.AddSeconds(8));
        tl.Add(102, 4, T0.AddSeconds(12));
        return tl;
    }

    [Fact]
    public void Positions_inside_a_segment_map_to_its_content_time()
    {
        var tl = Three();

        var (utc, seq) = tl.Resolve(0);
        Assert.Equal(T0, utc);
        Assert.Equal(100, seq);

        (utc, seq) = tl.Resolve(5.5);
        Assert.Equal(T0.AddSeconds(5.5), utc);
        Assert.Equal(101, seq);

        (utc, seq) = tl.Resolve(11.999);
        Assert.Equal(T0.AddSeconds(11.999), utc, TimeSpan.FromMilliseconds(1));
        Assert.Equal(102, seq);
    }

    [Fact]
    public void Positions_past_the_fed_audio_extrapolate_from_the_last_segment()
    {
        var tl = Three();
        Assert.Equal(12, tl.FedSeconds);

        var (utc, seq) = tl.Resolve(13);
        Assert.Equal(T0.AddSeconds(13), utc);
        Assert.Equal(102, seq);
    }

    [Fact]
    public void Init_and_zero_length_segments_are_ignored()
    {
        var tl = new SegmentTimeline();
        tl.Add(99, 0, T0);          // init segment (Duration = 0)
        tl.Add(100, 4, T0.AddSeconds(4));

        Assert.Equal(1, tl.Count);
        Assert.Equal(4, tl.FedSeconds);
    }

    [Fact]
    public void Capture_gaps_are_reflected_in_wall_clock_but_not_in_the_audio_clock()
    {
        // Segment 101 was fetched 10 s late (stall) — the audio clock stays contiguous (4 s + 4 s),
        // the resolved wall-clock jumps with the capture time.
        var tl = new SegmentTimeline();
        tl.Add(100, 4, T0.AddSeconds(4));
        tl.Add(101, 4, T0.AddSeconds(18));

        Assert.Equal(8, tl.FedSeconds);
        Assert.Equal(T0.AddSeconds(14), tl.Resolve(4).Utc);
    }

    [Fact]
    public void Old_entries_are_pruned_but_resolution_stays_correct_for_recent_audio()
    {
        var tl = new SegmentTimeline(keepSeconds: 10);
        for (var i = 0; i < 50; i++)
            tl.Add(100 + i, 2, T0.AddSeconds(2 * (i + 1)));

        var (utc, seq) = tl.Resolve(99);     // inside the last segment (98–100)
        Assert.Equal(T0.AddSeconds(99), utc);
        Assert.Equal(149, seq);
        Assert.True(tl.Count < 50, "entries far behind the resolved position should have been dropped");

        // Something older than the kept window still resolves (extrapolated from the oldest kept entry).
        var (oldUtc, _) = tl.Resolve(0);
        Assert.Equal(T0, oldUtc);
    }

    [Fact]
    public void Empty_timeline_falls_back_to_now()
    {
        var tl = new SegmentTimeline();
        var (utc, seq) = tl.Resolve(3);
        Assert.Equal(DateTime.UtcNow, utc, TimeSpan.FromSeconds(5));
        Assert.Equal(-1, seq);
    }
}
