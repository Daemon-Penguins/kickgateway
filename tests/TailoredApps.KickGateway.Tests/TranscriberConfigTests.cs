using TailoredApps.KickGateway.Subscribers.Transcriber;
using TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;
using Whisper.net.Ggml;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class TranscriberConfigTests
{
    [Theory]
    [InlineData("LargeV3Turbo", GgmlType.LargeV3Turbo)]
    [InlineData("large-v3-turbo", GgmlType.LargeV3Turbo)]
    [InlineData("largev3turbo", GgmlType.LargeV3Turbo)]
    [InlineData("small", GgmlType.Small)]
    [InlineData("BASE", GgmlType.Base)]
    public void Model_names_are_parsed_loosely(string input, GgmlType expected)
        => Assert.Equal(expected, WhisperModelProvider.ParseEnum<GgmlType>(input, "Model"));

    [Theory]
    [InlineData("Q5_0", QuantizationType.Q5_0)]
    [InlineData("q5-0", QuantizationType.Q5_0)]
    [InlineData("q8_0", QuantizationType.Q8_0)]
    [InlineData("NoQuantization", QuantizationType.NoQuantization)]
    public void Quantization_names_are_parsed_loosely(string input, QuantizationType expected)
        => Assert.Equal(expected, WhisperModelProvider.ParseEnum<QuantizationType>(input, "Quantization"));

    [Fact]
    public void Unknown_model_name_lists_the_valid_ones()
    {
        var ex = Assert.Throws<ArgumentException>(() => WhisperModelProvider.ParseEnum<GgmlType>("gigantic", "Model"));
        Assert.Contains("Transcriber:Model", ex.Message);
        Assert.Contains("LargeV3Turbo", ex.Message);
    }

    [Fact]
    public void Default_options_validate()
    {
        new TranscriberOptions().Validate();
    }

    [Theory]
    [InlineData(31, 2)]   // above Whisper's 30 s window
    [InlineData(2, 1)]    // too short to be useful
    [InlineData(15, 15)]  // min must stay below target
    public void Inconsistent_chunking_is_rejected(double chunk, double min)
    {
        var opts = new TranscriberOptions { ChunkSeconds = chunk, MinChunkSeconds = min };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Fact]
    public void Session_timeout_must_cover_the_idle_flush()
    {
        var opts = new TranscriberOptions { IdleFlushSeconds = 30, SessionTimeoutSeconds = 10 };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Fact]
    public void Default_language_setup_detects_among_pl_en_de_with_pl_as_fallback()
    {
        var opts = new TranscriberOptions();
        Assert.True(opts.DetectsLanguage);
        Assert.False(opts.IsAutoLanguage);
        Assert.Equal(["pl", "en", "de"], opts.CandidateLanguages);
        Assert.Equal("pl", opts.FallbackLanguage);
        Assert.Contains("detecting among pl,en,de", opts.DescribeLanguageMode());
        Assert.Equal("pl, detecting among pl,en,de (switch after 2 chunk(s) at p>=0.70)", opts.DescribeLanguageMode());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Confirmation_chunks_must_be_between_one_and_ten(int chunks)
    {
        var opts = new TranscriberOptions { LanguageSwitchConfirmChunks = chunks };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Fact]
    public void Candidate_list_is_parsed_loosely_and_always_contains_the_fallback()
    {
        var opts = new TranscriberOptions { Language = "EN", Languages = " de ; PL,de " };
        Assert.Equal(["en", "de", "pl"], opts.CandidateLanguages); // fallback first, duplicates and case folded
        Assert.Equal("en", opts.FallbackLanguage);
        opts.Validate();
    }

    [Fact]
    public void Empty_candidate_list_means_a_fixed_language()
    {
        var opts = new TranscriberOptions { Language = "pl", Languages = "" };
        Assert.False(opts.DetectsLanguage);
        Assert.Empty(opts.CandidateLanguages);
        Assert.Equal("pl (fixed)", opts.DescribeLanguageMode());
        opts.Validate();
    }

    [Fact]
    public void Auto_with_candidates_falls_back_to_the_first_candidate()
    {
        var opts = new TranscriberOptions { Language = "auto", Languages = "de,pl" };
        Assert.True(opts.IsAutoLanguage);
        Assert.True(opts.DetectsLanguage);
        Assert.Equal(["de", "pl"], opts.CandidateLanguages);
        Assert.Equal("de", opts.FallbackLanguage);
        opts.Validate();

        var plainAuto = new TranscriberOptions { Language = "auto", Languages = "" };
        Assert.False(plainAuto.DetectsLanguage);
        Assert.Equal("auto (Whisper picks per chunk)", plainAuto.DescribeLanguageMode());
        plainAuto.Validate();
    }

    [Theory]
    [InlineData("polish")]
    [InlineData("pl,german")]
    [InlineData("pl,e1")]
    public void Candidate_codes_must_be_iso_639_1(string languages)
    {
        var opts = new TranscriberOptions { Languages = languages };
        var ex = Assert.Throws<ArgumentException>(() => opts.Validate());
        Assert.Contains("Transcriber:Languages", ex.Message);
    }

    [Fact]
    public void Fallback_language_must_be_a_code_or_auto()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TranscriberOptions { Language = "polish" }.Validate());
        Assert.Contains("Transcriber:Language", ex.Message);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.01f)]
    [InlineData(-0.5f)]
    public void Switch_probability_must_be_within_zero_exclusive_to_one(float p)
    {
        var opts = new TranscriberOptions { LanguageSwitchMinProbability = p };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }
}
