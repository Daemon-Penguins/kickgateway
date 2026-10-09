namespace TailoredApps.KickGateway.Subscribers.Transcriber.Audio;

/// <summary>
/// Maps a position on the channel's audio clock (seconds of decoded audio since the session
/// started) back to stream time. Every media segment fed to the decoder is recorded with its
/// <c>EXTINF</c> duration and capture time; a segment's content is assumed to end roughly when the
/// listener fetched it (<c>CapturedAt</c>), so its content starts at <c>CapturedAt − Duration</c>.
/// Resolution is therefore accurate to about one segment. Pure and synchronous.
/// </summary>
public sealed class SegmentTimeline
{
    private readonly record struct Entry(double Start, double Duration, DateTime ContentStart, long MediaSequence)
    {
        public double End => Start + Duration;
    }

    private readonly List<Entry> _entries = new();
    private readonly double _keepSeconds;

    public SegmentTimeline(double keepSeconds = 120) => _keepSeconds = keepSeconds;

    /// <summary>Total seconds of media fed so far (the audio clock's upper bound).</summary>
    public double FedSeconds { get; private set; }

    public int Count => _entries.Count;

    /// <summary>Records one media segment. Init segments and zero-length entries are ignored.</summary>
    public void Add(long mediaSequence, double duration, DateTime capturedAt)
    {
        if (duration <= 0) return;
        _entries.Add(new Entry(FedSeconds, duration, capturedAt.AddSeconds(-duration), mediaSequence));
        FedSeconds += duration;
    }

    /// <summary>Resolves an audio-clock position to an estimated UTC time and the segment that contains it.</summary>
    public (DateTime Utc, long MediaSequence) Resolve(double audioSeconds)
    {
        if (_entries.Count == 0) return (DateTime.UtcNow, -1);

        Prune(audioSeconds);

        var first = _entries[0];
        if (audioSeconds < first.Start)
            return (first.ContentStart.AddSeconds(audioSeconds - first.Start), first.MediaSequence);

        foreach (var e in _entries)
        {
            if (audioSeconds < e.End)
                return (e.ContentStart.AddSeconds(audioSeconds - e.Start), e.MediaSequence);
        }

        var last = _entries[^1];
        return (last.ContentStart.AddSeconds(audioSeconds - last.Start), last.MediaSequence);
    }

    /// <summary>Forgets segments that ended long before <paramref name="audioSeconds"/>; the decoder never looks that far back.</summary>
    private void Prune(double audioSeconds)
    {
        var cutoff = audioSeconds - _keepSeconds;
        var remove = 0;
        while (remove < _entries.Count - 1 && _entries[remove].End < cutoff) remove++;
        if (remove > 0) _entries.RemoveRange(0, remove);
    }
}
