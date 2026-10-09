namespace TailoredApps.KickGateway.Subscribers.Transcriber.Audio;

/// <summary>
/// A contiguous run of 16 kHz mono PCM cut by the <see cref="AudioChunker"/>. Sample positions are
/// absolute on the channel's audio clock (samples since the capture session started), so the
/// session can map them back to stream time.
/// </summary>
public sealed record AudioChunk(float[] Samples, long StartSample, long EndSample)
{
    public int Length => Samples.Length;

    public double Seconds(int sampleRate) => (double)Samples.Length / sampleRate;

    /// <summary>Root-mean-square level, 0..1. Silence detection uses it to skip Whisper entirely.</summary>
    public float Rms() => Rms(Samples);

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0f;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
