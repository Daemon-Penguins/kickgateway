using TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class TranscriptFilterTests
{
    private static readonly TranscriptFilter Filter = new(0.35f, ["Dziękuję za oglądanie", "Thanks for watching"]);

    [Theory]
    [InlineData("No dobra, to lecimy dalej z tym questem.", 0.9f)]
    [InlineData("okay so what happened next was wild", 0.5f)]
    [InlineData("Chat, 1v1 me", 0f)] // unknown probability (0) is never a reason to drop
    public void Normal_speech_is_accepted(string text, float p)
    {
        Assert.True(Filter.Accept(text, p, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("", "blank")]
    [InlineData("   ", "blank")]
    [InlineData("...", "blank")]
    [InlineData("[muzyka]", "non-speech")]
    [InlineData("(śmiech) ♪♪", "non-speech")]
    [InlineData("*laughs*", "non-speech")]
    public void Blank_and_sound_tags_are_dropped(string text, string expected)
    {
        Assert.False(Filter.Accept(text, 0.9f, out var reason));
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData("Dziękuję za oglądanie!")]
    [InlineData("dziękuję za oglądanie.")]
    [InlineData("Dziękuje za oglądanie.")]   // seen live: Whisper dropped the ogonek — diacritics must not matter
    [InlineData("Dziekuje za ogladanie")]
    [InlineData("Thanks for watching, bye")] // phrase dominates a short segment
    public void Known_hallucination_phrases_are_dropped(string text)
    {
        Assert.False(Filter.Accept(text, 0.9f, out var reason));
        Assert.Equal("suppressed-phrase", reason);
    }

    [Fact]
    public void Suppress_phrase_inside_real_speech_is_kept()
    {
        // The phrase is a small part of a long, genuine sentence — don't throw the sentence away.
        Assert.True(Filter.Accept("Na koniec streama zawsze mówię dziękuję za oglądanie i zapraszam jutro o tej samej porze na kolejny odcinek", 0.9f, out _));
    }

    [Theory]
    [InlineData("nie nie nie nie nie nie nie nie nie nie")]
    [InlineData("ha ha ha ha ha ha ha ha ha ha ha ha")]
    [InlineData("Tak, tak. Tak, tak. Tak, tak. Tak, tak. Tak, tak.")]
    public void Degenerate_repetition_is_dropped(string text)
    {
        Assert.False(Filter.Accept(text, 0.9f, out var reason));
        Assert.Equal("repetitive", reason);
    }

    [Fact]
    public void Short_repeats_are_fine()
    {
        Assert.True(Filter.Accept("nie nie nie", 0.9f, out _)); // fewer than 8 words → never "repetitive"
    }

    [Fact]
    public void Low_confidence_is_dropped_only_below_the_floor()
    {
        Assert.False(Filter.Accept("coś tam mamrocze", 0.2f, out var reason));
        Assert.Equal("low-confidence", reason);
        Assert.True(Filter.Accept("coś tam mamrocze", 0.35f, out _));
    }

    [Fact]
    public void Confidence_floor_of_zero_disables_the_check()
    {
        var permissive = new TranscriptFilter(0f, []);
        Assert.True(permissive.Accept("whatever", 0.01f, out _));
    }

    [Fact]
    public void Loop_of_identical_consecutive_segments_is_dropped_as_a_whole()
    {
        // Seen on a live stream: one short sentence emitted five times in a row — each passes Accept alone.
        var segs = new[]
        {
            "Dobra, lecimy.",
            "Zobaczcie, że nie ma się z nimi.",
            "Zobaczcie, że nie ma się z nimi!",
            "zobaczcie, że nie ma się z nimi",
            "Zobaczcie, że nie ma się z nimi.",
            "No i tyle.",
        };

        var dropped = new List<(int Run, string Text)>();
        var kept = TranscriptFilter.DropLoops(segs, s => s, onDropped: (run, text) => dropped.Add((run, text)));

        Assert.Equal(["Dobra, lecimy.", "No i tyle."], kept);
        Assert.Single(dropped);
        Assert.Equal(4, dropped[0].Run);
    }

    [Fact]
    public void Saying_something_twice_is_not_a_loop()
    {
        var segs = new[] { "Będzie pani zadowolona.", "Będzie pani zadowolona.", "A ty już zrobiłeś?" };
        Assert.Equal(segs, TranscriptFilter.DropLoops(segs, s => s));
    }

    [Theory]
    [InlineData("Dziękuję, za   oglądanie!!!", "dziekuje za ogladanie")]
    [InlineData("Łódź żółć", "lodz zolc")]
    [InlineData("  Hello, World.  ", "hello world")]
    [InlineData("Amara.org", "amara org")]
    public void Normalize_strips_punctuation_case_and_diacritics(string input, string expected)
        => Assert.Equal(expected, TranscriptFilter.Normalize(input));
}
