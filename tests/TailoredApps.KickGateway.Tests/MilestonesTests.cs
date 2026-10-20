using TailoredApps.KickGateway.Api.LiveFeed;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>The landing page's million-message milestones: baseline on open, fire once per million, never on a wobble.</summary>
public class MilestonesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(999_999, 0)]
    [InlineData(1_000_000, 1)]
    [InlineData(2_999_999, 2)]
    [InlineData(3_000_000, 3)]
    public void Reached_counts_full_millions(long count, long expected) =>
        Assert.Equal(expected, Milestones.Reached(count));

    [Fact]
    public void Crossing_the_next_million_fires_that_milestone_once()
    {
        var highest = Milestones.Reached(1_999_950);     // page opened just below 2M: baseline, no confetti
        Assert.Equal(1, highest);

        Assert.Equal(0, Milestones.Crossed(highest, 1_999_999));
        Assert.Equal(2_000_000, Milestones.Crossed(highest, 2_000_000));

        highest = Milestones.Reached(2_000_000);
        Assert.Equal(0, Milestones.Crossed(highest, 2_000_001));
        Assert.Equal(0, Milestones.Crossed(highest, 1_999_990));  // a count that wobbles back below does not re-arm
        Assert.Equal(0, Milestones.Crossed(highest, 2_000_000));  // ...and does not fire again when it comes back
    }

    [Fact]
    public void Several_millions_at_once_celebrate_the_highest()
    {
        Assert.Equal(5_000_000, Milestones.Crossed(2, 5_250_000));
    }

    [Fact]
    public void Reaching_the_first_million_from_zero_fires()
    {
        Assert.Equal(1_000_000, Milestones.Crossed(0, 1_000_000));
        Assert.Equal(0, Milestones.Crossed(0, 999_999));
    }
}
