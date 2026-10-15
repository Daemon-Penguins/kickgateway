namespace TailoredApps.TwitchGateway.Capture;

/// <summary>Configuration for the Twitch audio capture, bound from the <c>Twitch:Capture</c> section.</summary>
public class TwitchCaptureOptions
{
    public const string SectionName = "Twitch:Capture";

    /// <summary>Global switch. Off = the process idles (so the container can run without capturing).</summary>
    public bool Enabled { get; set; }

    /// <summary>Rendition name to pull. <c>audio_only</c> (~160 kb/s AAC in MPEG-TS) is all the transcriber needs.</summary>
    public string Rendition { get; set; } = "audio_only";

    /// <summary>When <see cref="Rendition"/> is not offered: the highest video rendition under this cap (kbps).</summary>
    public int MaxBitrateKbps { get; set; } = 1500;

    /// <summary>How often the roster is re-read and the live state checked (Helix when the app is configured, usher otherwise).</summary>
    public int LiveCheckSeconds { get; set; } = 30;

    /// <summary>Broker TTL on each segment: absent consumers must not pile them up.</summary>
    public int SegmentTtlSeconds { get; set; } = 45;

    /// <summary>Segments above this are skipped (a corrupt playlist / wrong rendition guard).</summary>
    public int MaxSegmentBytes { get; set; } = 6 * 1024 * 1024;

    /// <summary>Skip segments inside Twitch's stitched ad breaks (otherwise the transcriber would transcribe the ads).</summary>
    public bool SkipAds { get; set; } = true;

    /// <summary>Stop a capture loop when the playlist has not advanced for this long (stream ended, transcode gone); the live check restarts it.</summary>
    public int IdleStopSeconds { get; set; } = 60;

    /// <summary>Playlist poll interval in ms (0 = one segment duration, min 1000).</summary>
    public int PollIntervalMs { get; set; }

    /// <summary>Capture only these logins (comma-separated); empty = every channel with CaptureAudio on. For running a second capture box on a subset.</summary>
    public string Channels { get; set; } = "";

    public string[] ChannelList => (Channels ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => c.ToLowerInvariant()).ToArray();
}
