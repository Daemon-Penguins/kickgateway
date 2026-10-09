namespace TailoredApps.KickGateway.Subscribers.Transcriber;

/// <summary>One chunk of audio waiting for Whisper, with everything needed to stamp the result.</summary>
public sealed record TranscriptionJob(
    string Slug,
    string ChannelId,
    float[] Samples,
    double AudioStartSeconds,
    DateTime StartedAt,
    DateTime EndedAt,
    long FirstMediaSequence,
    long LastMediaSequence,
    DateTime EnqueuedAt)
{
    public double Seconds => (double)Samples.Length / AudioDecoder.SampleRate;
}
