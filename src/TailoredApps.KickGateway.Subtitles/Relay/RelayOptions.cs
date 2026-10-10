using System.Security.Cryptography;
using System.Text;

namespace TailoredApps.KickGateway.Subtitles.Relay;

/// <summary>
/// The delayed-video relay (section <c>Subtitles:Relay</c>). Off unless a token of at least
/// <see cref="MinTokenLength"/> characters is configured — the relay costs outbound transfer per viewer,
/// so it is never public.
/// </summary>
public sealed class RelayOptions
{
    public const string Section = "Subtitles:Relay";
    public const int MinTokenLength = 16;

    /// <summary>Shared secret viewers pass as <c>?token=</c>. Empty or shorter than <see cref="MinTokenLength"/> = relay disabled.</summary>
    public string? Token { get; set; }

    /// <summary>How much video to keep per channel. Must leave room above <see cref="MaxDelaySeconds"/> for the player's own buffer.</summary>
    public int BufferSeconds { get; set; } = 90;

    /// <summary>Hard cap on buffered bytes per channel (segments are ~2 s; at 1.5 Mbps 90 s is ~17 MB).</summary>
    public int MaxBufferMegabytesPerChannel { get; set; } = 64;

    /// <summary>How far behind the live edge the player starts when the page has no <c>?delay=</c>.</summary>
    public int DefaultDelaySeconds { get; set; } = 15;

    /// <summary>Largest delay a viewer may request.</summary>
    public int MaxDelaySeconds { get; set; } = 60;

    /// <summary>Concurrent viewers per channel; the rest get 429. The transfer bound: viewers × bitrate.</summary>
    public int MaxViewersPerChannel { get; set; } = 3;

    /// <summary>A viewer that has not polled the playlist for this long frees its slot.</summary>
    public int ViewerIdleSeconds { get; set; } = 15;

    /// <summary>Comma-separated channels to buffer. Empty = the site's <c>Subtitles:Channels</c>; both empty = every live channel on the exchange (memory!).</summary>
    public string Channels { get; set; } = "";

    public bool Enabled => (Token ?? "").Trim().Length >= MinTokenLength;

    /// <summary>Constant-time token comparison; false when the relay is disabled.</summary>
    public bool TokenMatches(string? candidate)
    {
        if (!Enabled || string.IsNullOrEmpty(candidate)) return false;
        var expected = Encoding.UTF8.GetBytes(Token!.Trim());
        var actual = Encoding.UTF8.GetBytes(candidate.Trim());
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public string[] NormalizedChannels => SubtitlesOptions.ParseList(Channels);

    public int ClampDelay(int? requested) =>
        Math.Clamp(requested ?? DefaultDelaySeconds, 0, MaxDelaySeconds);

    public void Validate()
    {
        if (BufferSeconds is < 20 or > 600) throw new ArgumentException("Subtitles:Relay:BufferSeconds must be between 20 and 600.");
        if (MaxDelaySeconds < 0 || MaxDelaySeconds > BufferSeconds - 10) throw new ArgumentException("Subtitles:Relay:MaxDelaySeconds must be at least 10 s below BufferSeconds.");
        if (DefaultDelaySeconds < 0 || DefaultDelaySeconds > MaxDelaySeconds) throw new ArgumentException("Subtitles:Relay:DefaultDelaySeconds must be between 0 and MaxDelaySeconds.");
        if (MaxBufferMegabytesPerChannel is < 4 or > 1024) throw new ArgumentException("Subtitles:Relay:MaxBufferMegabytesPerChannel must be between 4 and 1024.");
        if (MaxViewersPerChannel is < 1 or > 100) throw new ArgumentException("Subtitles:Relay:MaxViewersPerChannel must be between 1 and 100.");
        if (ViewerIdleSeconds is < 5 or > 300) throw new ArgumentException("Subtitles:Relay:ViewerIdleSeconds must be between 5 and 300.");
        foreach (var c in NormalizedChannels)
            if (!SubtitlesOptions.TryNormalizeSlug(c, out _)) throw new ArgumentException($"Subtitles:Relay:Channels contains '{c}' — slugs are lowercase letters, digits, '_' and '-'.");
        if (!string.IsNullOrWhiteSpace(Token) && !Enabled)
            throw new ArgumentException($"Subtitles:Relay:Token must be at least {MinTokenLength} characters (or empty to keep the relay off).");
    }
}
