using TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// A channel speaks the fallback language until the detector reports the same other language
/// confidently in N consecutive chunks; unsure readings and readings of the current language
/// break the streak, and every channel has its own state.
/// </summary>
public class LanguageTrackerTests
{
    private static LanguageTracker Tracker(float min = 0.7f, int confirm = 1) => new("pl", min, confirm);

    [Fact]
    public void Channel_starts_in_the_fallback_language()
    {
        var t = Tracker();
        Assert.Equal("pl", t.Current("streamer"));
        Assert.Equal("pl", t.Fallback);
    }

    [Fact]
    public void Confident_other_language_switches_the_channel_when_one_confirmation_is_enough()
    {
        var t = Tracker(confirm: 1);
        var d = t.Decide("streamer", "de", 0.83f);

        Assert.Equal("de", d.Language);
        Assert.Equal("de", d.Detected);
        Assert.Equal(0.83f, d.Probability);
        Assert.True(d.Switched);
        Assert.Equal(0, d.PendingConfirmations);
        Assert.Equal("de", t.Current("streamer"));
    }

    [Fact]
    public void Unsure_other_language_keeps_the_current_one_but_reports_what_was_heard()
    {
        var t = Tracker();
        var d = t.Decide("streamer", "de", 0.41f);

        Assert.Equal("pl", d.Language);  // transcribed in the channel's language
        Assert.Equal("de", d.Detected);  // ...but the reading is kept for the record
        Assert.Equal(0.41f, d.Probability);
        Assert.False(d.Switched);
        Assert.Equal("pl", t.Current("streamer"));
    }

    [Fact]
    public void Same_language_is_never_a_switch_whatever_the_probability()
    {
        var t = Tracker();
        var d = t.Decide("streamer", "PL", 0.2f);

        Assert.Equal("pl", d.Language);
        Assert.Equal("pl", d.Detected);
        Assert.False(d.Switched);
    }

    [Fact]
    public void Threshold_is_inclusive()
    {
        var t = Tracker(0.7f);
        Assert.True(t.Decide("a", "en", 0.7f).Switched);
        Assert.False(t.Decide("b", "en", 0.69f).Switched);
    }

    [Fact]
    public void Switch_needs_the_same_other_language_confidently_n_chunks_in_a_row()
    {
        var t = Tracker(confirm: 2);

        var first = t.Decide("streamer", "de", 0.9f);
        Assert.False(first.Switched);
        Assert.Equal("pl", first.Language);          // still transcribed in Polish
        Assert.Equal("de", first.Detected);
        Assert.Equal(1, first.PendingConfirmations);

        var second = t.Decide("streamer", "de", 0.8f);
        Assert.True(second.Switched);
        Assert.Equal("de", second.Language);
        Assert.Equal(0, second.PendingConfirmations);
        Assert.Equal("de", t.Current("streamer"));
    }

    [Fact]
    public void A_single_confident_chunk_of_music_or_a_quote_does_not_flip_the_channel()
    {
        var t = Tracker(confirm: 2);

        Assert.False(t.Decide("streamer", "en", 0.99f).Switched); // English song playing
        Assert.False(t.Decide("streamer", "pl", 0.9f).Switched);  // back to the streamer talking → streak broken
        Assert.Equal("pl", t.Current("streamer"));

        Assert.Equal(1, t.Decide("streamer", "en", 0.9f).PendingConfirmations); // starts over
        Assert.Equal(0, t.Decide("streamer", "en", 0.5f).PendingConfirmations); // an unsure reading breaks the streak too
        Assert.False(t.Decide("streamer", "en", 0.9f).Switched);
        Assert.Equal("pl", t.Current("streamer"));
    }

    [Fact]
    public void Alternating_confident_other_languages_never_accumulate()
    {
        var t = Tracker(confirm: 2);
        Assert.Equal(1, t.Decide("streamer", "en", 0.9f).PendingConfirmations);
        Assert.Equal(1, t.Decide("streamer", "de", 0.9f).PendingConfirmations); // a different language restarts the count
        Assert.False(t.Decide("streamer", "en", 0.9f).Switched);
        Assert.Equal("pl", t.Current("streamer"));
    }

    [Fact]
    public void Detector_failure_and_short_chunks_keep_the_language_and_the_streak()
    {
        var t = Tracker(confirm: 2);
        t.Decide("streamer", "de", 0.9f);

        var failed = t.Decide("streamer", null, 0f);
        Assert.Equal("pl", failed.Language);
        Assert.Null(failed.Detected);
        Assert.Null(failed.Probability);
        Assert.Equal(1, failed.PendingConfirmations); // a missing reading is not evidence against the streak

        var kept = t.Keep("streamer");
        Assert.Equal("pl", kept.Language);
        Assert.Null(kept.Detected);
        Assert.Equal(1, kept.PendingConfirmations);

        Assert.True(t.Decide("streamer", "de", 0.9f).Switched); // the second confident German chunk completes it
    }

    [Fact]
    public void Channels_are_independent_and_switching_back_needs_confirmation_too()
    {
        var t = Tracker(confirm: 2);
        t.Decide("german", "de", 0.95f);
        t.Decide("german", "de", 0.95f);
        Assert.Equal("de", t.Current("german"));

        Assert.Equal("pl", t.Current("polish"));               // the other channel is untouched
        Assert.False(t.Decide("german", "pl", 0.9f).Switched);  // one Polish chunk is not enough
        Assert.True(t.Decide("german", "pl", 0.9f).Switched);
        Assert.Equal("pl", t.Current("german"));
    }

    [Fact]
    public void Fallback_is_normalized_and_arguments_are_validated()
    {
        Assert.Equal("en", new LanguageTracker(" EN ", 0.5f).Fallback);
        Assert.Throws<ArgumentException>(() => new LanguageTracker(" ", 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LanguageTracker("pl", 0.5f, 0));
    }
}
