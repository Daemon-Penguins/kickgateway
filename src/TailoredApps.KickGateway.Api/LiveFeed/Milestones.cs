namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>
/// The landing page celebrates every full million chat messages stored. A page compares the milestone index of
/// each totals refresh with the highest index it has seen; the first refresh only sets the baseline, so opening
/// the page never fires, and the index is monotonic so a count that wobbles around a boundary (or a rebuilt
/// read model counting up again) cannot fire the same million twice in one visit.
/// </summary>
public static class Milestones
{
    /// <summary>Messages between two celebrations.</summary>
    public const long Step = 1_000_000;

    /// <summary>How many full steps <paramref name="count"/> has reached (0 below the first).</summary>
    public static long Reached(long count) => count <= 0 ? 0 : count / Step;

    /// <summary>
    /// The milestone value (1,000,000, 2,000,000, ...) newly reached by <paramref name="count"/> beyond
    /// <paramref name="highestReached"/>, or 0 when none. Several steps crossed at once celebrate the highest.
    /// </summary>
    public static long Crossed(long highestReached, long count)
    {
        var now = Reached(count);
        return now > highestReached ? now * Step : 0;
    }
}
