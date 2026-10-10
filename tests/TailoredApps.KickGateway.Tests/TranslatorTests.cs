using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.Translator;
using TailoredApps.KickGateway.Subscribers.Translator.Translation;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// The translator's pure parts: which slices get a provider call, how segments go to the model as
/// numbered lines and come back aligned, and the configuration rules.
/// </summary>
public class TranslatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 16, 0, 0, DateTimeKind.Utc);

    private static TranslatorOptions Options() => new()
    {
        SourceLanguages = "de, EN",
        TargetLanguage = "pl",
        MinConfidence = 0.45f,
        MaxAgeSeconds = 120,
        Llm = new LlmOptions { Provider = "stub" },
    };

    private static LiveTranscript Transcript(string language = "de", string text = "Guten Abend", float confidence = 0.8f, DateTime? transcribedAt = null, string slug = "chef_jan") => new()
    {
        BroadcasterSlug = slug,
        StartedAt = T0,
        EndedAt = T0.AddSeconds(12),
        AudioStartSeconds = 30,
        Text = text,
        Language = language,
        Confidence = confidence,
        TranscribedAt = transcribedAt ?? T0.AddSeconds(13),
        Segments = [new LiveTranscriptSegment { StartedAt = T0, EndedAt = T0.AddSeconds(12), Text = text, Confidence = confidence }],
    };

    // ---- policy ----

    [Fact]
    public void German_slice_is_translated_and_the_rest_is_skipped_for_a_reason()
    {
        var o = Options();
        var now = T0.AddSeconds(20);
        Assert.Null(TranslationPolicy.SkipReason(Transcript("de"), o, now));
        Assert.Null(TranslationPolicy.SkipReason(Transcript("EN"), o, now));                    // case-insensitive source list
        Assert.Equal("already-target", TranslationPolicy.SkipReason(Transcript("pl"), o, now));
        Assert.Equal("language-not-configured", TranslationPolicy.SkipReason(Transcript("fr"), o, now));
        Assert.Equal("no-language", TranslationPolicy.SkipReason(Transcript(""), o, now));
        Assert.Equal("empty", TranslationPolicy.SkipReason(Transcript("de", text: "  "), o, now));
        Assert.Equal("low-confidence", TranslationPolicy.SkipReason(Transcript("de", confidence: 0.3f), o, now));
        Assert.Null(TranslationPolicy.SkipReason(Transcript("de", confidence: 0f), o, now));     // 0 = unknown, never a reason
        Assert.Equal("stale", TranslationPolicy.SkipReason(Transcript("de", transcribedAt: T0), o, T0.AddSeconds(121)));
    }

    [Fact]
    public void Channel_allowlist_is_applied_when_set()
    {
        var o = Options();
        o.Channels = "chef_jan, Trazer";
        Assert.Null(TranslationPolicy.SkipReason(Transcript("de", slug: "Chef_Jan"), o, T0));
        Assert.Equal("channel-not-configured", TranslationPolicy.SkipReason(Transcript("de", slug: "azdus"), o, T0));
        Assert.Equal(["chef_jan", "trazer"], o.NormalizedChannels);
    }

    // ---- prompt / parsing ----

    [Fact]
    public void Segments_become_numbered_lines_and_newlines_are_flattened()
    {
        var lines = TranslationPrompt.NumberedLines(["Guten Abend", "wie\ngeht's", " alles klar "]);
        Assert.Equal("1: Guten Abend\n2: wie geht's\n3: alles klar\n", lines);
    }

    [Fact]
    public void System_prompt_names_the_languages_and_can_be_overridden()
    {
        var prompt = TranslationPrompt.SystemPrompt("de", "pl");
        Assert.Contains("from German into Polish", prompt);
        Assert.Contains("never censor", prompt);
        Assert.Equal("X de→pl", TranslationPrompt.SystemPrompt("de", "pl", "X {source}→{target}").Replace("German", "de").Replace("Polish", "pl"));
    }

    [Theory]
    [InlineData("1: Dobry wieczór\n2: jak leci\n3: wszystko jasne")]
    [InlineData("1. Dobry wieczór\n2) jak leci\n3 - wszystko jasne\n")]
    [InlineData("Here you go:\n\n1: Dobry wieczór\n  2: jak leci  \n3: wszystko jasne\n\nLet me know if you need more.")]
    public void Numbered_output_is_aligned_back_onto_the_segments(string output)
    {
        var aligned = TranslationPrompt.ParseNumbered(output, 3);
        Assert.NotNull(aligned);
        Assert.Equal(["Dobry wieczór", "jak leci", "wszystko jasne"], aligned);
    }

    [Theory]
    [InlineData("1: Dobry wieczór\n2: jak leci")]                       // one line short
    [InlineData("1: Dobry wieczór\n2: jak leci\n4: wszystko jasne")]   // wrong number
    [InlineData("Dobry wieczór, jak leci, wszystko jasne")]           // no numbering
    [InlineData("")]
    public void Broken_numbering_is_not_aligned_and_falls_back_to_the_whole_text(string output)
    {
        Assert.Null(TranslationPrompt.ParseNumbered(output, 3));
        var whole = TranslationPrompt.Unnumbered(output);
        Assert.DoesNotContain("1:", whole);
        if (output.Length > 0) Assert.Contains("Dobry", whole);
    }

    [Fact]
    public async Task Stub_provider_marks_every_line_and_keeps_the_numbering()
    {
        var stub = new StubTranslator(new LlmOptions { Provider = "stub", Model = "test" });
        var output = await stub.TranslateAsync("sys", "1: Guten Abend\n2: wie geht's\n", default);
        var aligned = TranslationPrompt.ParseNumbered(output, 2);
        Assert.NotNull(aligned);
        Assert.Equal("[test] Guten Abend", aligned[0]);
        Assert.Equal("stub", stub.Name);
    }

    // ---- options ----

    [Fact]
    public void Options_validate_languages_and_provider()
    {
        Options().Validate();
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { SourceLanguages = "", Llm = new LlmOptions { Provider = "stub" } }.Validate());
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { SourceLanguages = "de", TargetLanguage = "de", Llm = new LlmOptions { Provider = "stub" } }.Validate());
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { Llm = new LlmOptions { Provider = "anthropic", ApiKey = "" } }.Validate()); // key required
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { Llm = new LlmOptions { Provider = "deepl", ApiKey = "x" } }.Validate());

        var anthropic = new LlmOptions { Provider = "Anthropic", ApiKey = "k" };
        Assert.Equal("https://api.anthropic.com", anthropic.EffectiveBaseUrl);
        Assert.Equal("claude-haiku-5-5", anthropic.EffectiveModel);
        var openai = new LlmOptions { Provider = "openai", ApiKey = "k", BaseUrl = "http://litellm:4000/v1/" };
        Assert.Equal("http://litellm:4000/v1", openai.EffectiveBaseUrl);
        Assert.Equal("gpt-4o-mini", openai.EffectiveModel);
    }

    [Fact]
    public void Queue_name_is_unique_per_process_unless_configured()
    {
        Assert.StartsWith("translator-", new TranslatorOptions().EffectiveQueueName);
        Assert.NotEqual(new TranslatorOptions().EffectiveQueueName, new TranslatorOptions().EffectiveQueueName);
        Assert.Equal("fixed", new TranslatorOptions { QueueName = " fixed " }.EffectiveQueueName);
    }
}
