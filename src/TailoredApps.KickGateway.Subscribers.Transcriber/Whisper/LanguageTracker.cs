namespace TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;

/// <summary>
/// Outcome of the per-chunk language decision.
/// </summary>
/// <param name="Language">Language the chunk is transcribed in (the channel's current language after this decision).</param>
/// <param name="Detected">What the detector heard on this chunk; null when detection did not run (fixed language, chunk too short, detector failure).</param>
/// <param name="Probability">Detector probability for <paramref name="Detected"/>, 0..1; null when detection did not run.</param>
/// <param name="Switched">True when this chunk moved the channel to another language.</param>
/// <param name="PendingConfirmations">Confident readings of another language in a row so far (0 = none pending); the channel switches when they reach the configured count.</param>
public sealed record LanguageDecision(string Language, string? Detected, float? Probability, bool Switched, int PendingConfirmations = 0);

/// <summary>
/// Per-channel "sticky" language. Whisper's detector runs on every chunk, restricted to the configured
/// candidates, but a 15 s chunk of a stream is a noisy sample — background music, a clip in another
/// language, laughter, silence Whisper fills with "I'm sorry." — and on such chunks the detector is
/// often wrong with p 0.6–0.9. So a channel only switches when the detector reports the <b>same other
/// language, confidently, in N consecutive chunks</b>; any unsure reading, or a reading of the current
/// language, breaks the streak. A genuine switch (a streamer going German for a while) costs N−1 chunks
/// transcribed in the old language; a song or a quoted sentence never flips the channel. Channels start
/// in the fallback language and keep their language across capture sessions.
/// <para>Not thread-safe — the single Whisper loop is the only caller.</para>
/// </summary>
public sealed class LanguageTracker
{
    private sealed class State
    {
        public string Current = "";
        public string? Pending;
        public int PendingCount;
    }

    private readonly string _fallback;
    private readonly float _minProbability;
    private readonly int _confirmChunks;
    private readonly Dictionary<string, State> _channels = new(StringComparer.OrdinalIgnoreCase);

    public LanguageTracker(string fallbackLanguage, float minProbability, int confirmChunks = 1)
    {
        if (string.IsNullOrWhiteSpace(fallbackLanguage)) throw new ArgumentException("A fallback language is required.", nameof(fallbackLanguage));
        if (confirmChunks < 1) throw new ArgumentOutOfRangeException(nameof(confirmChunks), "At least one confident reading is needed to switch.");
        _fallback = fallbackLanguage.Trim().ToLowerInvariant();
        _minProbability = minProbability;
        _confirmChunks = confirmChunks;
    }

    public string Fallback => _fallback;

    /// <summary>The language <paramref name="slug"/> is currently transcribed in.</summary>
    public string Current(string slug) => _channels.TryGetValue(slug, out var s) ? s.Current : _fallback;

    /// <summary>No detection for this chunk (too short, detector failed): stay with the channel's language, streak untouched.</summary>
    public LanguageDecision Keep(string slug)
    {
        var state = Get(slug);
        return new LanguageDecision(state.Current, null, null, false, state.PendingCount);
    }

    /// <summary>Apply one detector reading for <paramref name="slug"/>.</summary>
    public LanguageDecision Decide(string slug, string? detected, float probability)
    {
        var state = Get(slug);
        if (string.IsNullOrWhiteSpace(detected))
            return new LanguageDecision(state.Current, null, null, false, state.PendingCount);

        detected = detected.Trim().ToLowerInvariant();
        if (string.Equals(detected, state.Current, StringComparison.OrdinalIgnoreCase))
        {
            Reset(state);
            return new LanguageDecision(state.Current, detected, probability, false);
        }

        if (probability < _minProbability)
        {
            Reset(state); // an unsure reading breaks the streak
            return new LanguageDecision(state.Current, detected, probability, false);
        }

        if (string.Equals(state.Pending, detected, StringComparison.OrdinalIgnoreCase))
            state.PendingCount++;
        else
        {
            state.Pending = detected;
            state.PendingCount = 1;
        }

        if (state.PendingCount >= _confirmChunks)
        {
            state.Current = detected;
            Reset(state);
            return new LanguageDecision(detected, detected, probability, true);
        }

        return new LanguageDecision(state.Current, detected, probability, false, state.PendingCount);
    }

    private State Get(string slug)
    {
        if (!_channels.TryGetValue(slug, out var state))
            _channels[slug] = state = new State { Current = _fallback };
        return state;
    }

    private static void Reset(State state)
    {
        state.Pending = null;
        state.PendingCount = 0;
    }
}
