using TailoredApps.KickGateway.Subscribers.Transcriber.Audio;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class AudioChunkerTests
{
    private const int Rate = 16_000;

    private static float[] Tone(double seconds, float amplitude = 0.5f)
    {
        var n = (int)(seconds * Rate);
        var buf = new float[n];
        for (var i = 0; i < n; i++) buf[i] = amplitude * MathF.Sin(2 * MathF.PI * 440 * i / Rate);
        return buf;
    }

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static float[] Concat(params float[][] parts)
    {
        var all = new float[parts.Sum(p => p.Length)];
        var off = 0;
        foreach (var p in parts) { p.CopyTo(all, off); off += p.Length; }
        return all;
    }

    [Fact]
    public void Nothing_is_emitted_below_the_target_length()
    {
        var chunker = new AudioChunker(Rate, targetSeconds: 10, minSeconds: 2);
        var out_ = new List<AudioChunk>();

        chunker.Append(Tone(9.9), out_);

        Assert.Empty(out_);
        Assert.Equal(9.9, chunker.BufferedSeconds, 2);
    }

    [Fact]
    public void Cut_lands_in_the_quiet_gap_before_the_target()
    {
        // 10 s target, 5 s search window: audio = 6 s tone, 0.5 s silence (6.0–6.5), tone until 12 s.
        var chunker = new AudioChunker(Rate, targetSeconds: 10, minSeconds: 2, searchWindowSeconds: 5);
        var out_ = new List<AudioChunk>();

        chunker.Append(Concat(Tone(6), Silence(0.5), Tone(5.5)), out_);

        var chunk = Assert.Single(out_);
        var cutSeconds = chunk.EndSample / (double)Rate;
        Assert.InRange(cutSeconds, 6.0, 6.5);
        Assert.Equal(0, chunk.StartSample);
        Assert.Equal(chunk.EndSample - chunk.StartSample, chunk.Length);
        // The remainder stays buffered and keeps its absolute position.
        Assert.Equal(12.0, chunker.Position / (double)Rate, 2);
        Assert.Equal(12.0 - cutSeconds, chunker.BufferedSeconds, 2);
    }

    [Fact]
    public void Without_a_quiet_spot_it_still_cuts_and_keeps_the_clock_continuous()
    {
        var chunker = new AudioChunker(Rate, targetSeconds: 5, minSeconds: 1, searchWindowSeconds: 2);
        var out_ = new List<AudioChunk>();

        chunker.Append(Tone(12), out_);

        Assert.Equal(2, out_.Count);
        Assert.Equal(0, out_[0].StartSample);
        Assert.Equal(out_[0].EndSample, out_[1].StartSample);
        Assert.Equal(12 * Rate, chunker.Position);
        foreach (var c in out_) Assert.InRange(c.Seconds(Rate), 3.0, 5.0);
    }

    [Fact]
    public void Flush_respects_the_minimum_unless_forced()
    {
        var chunker = new AudioChunker(Rate, targetSeconds: 10, minSeconds: 2);
        var out_ = new List<AudioChunk>();
        chunker.Append(Tone(1.5), out_);

        Assert.Null(chunker.Flush());
        var forced = chunker.Flush(force: true);
        Assert.NotNull(forced);
        Assert.Equal(1.5, forced!.Seconds(Rate), 2);
        Assert.Equal(0, chunker.Buffered);
        Assert.Null(chunker.Flush(force: true));
    }

    [Fact]
    public void Reset_drops_the_buffer_and_moves_the_clock()
    {
        var chunker = new AudioChunker(Rate, targetSeconds: 10, minSeconds: 2);
        var out_ = new List<AudioChunk>();
        chunker.Append(Tone(3), out_);

        chunker.Reset(100 * Rate);
        Assert.Equal(0, chunker.Buffered);
        Assert.Equal(100L * Rate, chunker.Position);

        chunker.Append(Tone(2.5), out_);
        var chunk = chunker.Flush()!;
        Assert.Equal(100L * Rate, chunk.StartSample);
    }

    [Fact]
    public void Rms_distinguishes_silence_from_speech_level_audio()
    {
        Assert.Equal(0f, AudioChunk.Rms(Silence(1)));
        Assert.InRange(AudioChunk.Rms(Tone(1, 0.5f)), 0.34f, 0.36f); // sine RMS = A/√2
        Assert.True(AudioChunk.Rms(Tone(1, 0.001f)) < 0.006f);
    }
}
