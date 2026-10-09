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
}
