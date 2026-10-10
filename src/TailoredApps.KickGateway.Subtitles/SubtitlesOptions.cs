using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TailoredApps.KickGateway.Subtitles;

/// <summary>Configuration of the live-subtitles site (section <c>Subtitles</c>).</summary>
public sealed partial class SubtitlesOptions
{
    public const string Section = "Subtitles";

    /// <summary>
    /// Comma-separated channel slugs the site serves (a plain string so a single env var can set it).
    /// Empty = any slug (the page embeds Kick's player for it and shows whatever transcripts arrive). Also
    /// the binding list for the transcript queue, so with an allowlist the service only receives those
    /// channels' transcripts.
    /// </summary>
    public string Channels { get; set; } = "";

    /// <summary>
    /// Receive queue name. Default: a unique per-process name. Subtitles are ephemeral — a late line is
    /// useless — so the queue is non-durable, auto-delete and never shared between instances.
    /// </summary>
    public string? QueueName { get; set; }

    /// <summary>How long a transcript stays in the per-channel backlog handed to newly connected viewers.</summary>
    public int BacklogSeconds { get; set; } = 180;

    /// <summary>Upper bound of backlog items per channel regardless of age.</summary>
    public int MaxBacklogItems { get; set; } = 60;

    /// <summary>Interval of SSE keep-alive comments, so proxies don't drop an idle stream (a quiet streamer).</summary>
    public int KeepAliveSeconds { get; set; } = 15;

    private string? _effectiveQueueName;

    /// <summary>Configured queue name, or <c>subtitles-{machine}-{random}</c> generated once per process.</summary>
    public string EffectiveQueueName => _effectiveQueueName ??= string.IsNullOrWhiteSpace(QueueName)
        ? $"subtitles-{Sanitize(Environment.MachineName)}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}"
        : QueueName.Trim();

    /// <summary>Lowercase slugs from <see cref="Channels"/>, blanks dropped.</summary>
    public string[] NormalizedChannels => ParseList(Channels);

    /// <summary>Comma/semicolon/space-separated slugs → lowercase, distinct.</summary>
    public static string[] ParseList(string? csv) => (csv ?? "")
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.ToLowerInvariant())
        .Distinct()
        .ToArray();

    /// <summary>True when the site serves <paramref name="slug"/> (already normalized).</summary>
    public bool Allows(string slug)
    {
        var list = NormalizedChannels;
        return list.Length == 0 || list.Contains(slug, StringComparer.Ordinal);
    }

    /// <summary>
    /// Kick slugs are lowercase letters, digits, <c>_</c> and <c>-</c>. Anything else (dots, slashes, path
    /// tricks) is rejected before it reaches the player URL or a queue binding.
    /// </summary>
    public static bool TryNormalizeSlug(string? raw, out string slug)
    {
        slug = (raw ?? "").Trim().ToLowerInvariant();
        return slug.Length > 0 && SlugPattern().IsMatch(slug);
    }

    public void Validate()
    {
        foreach (var c in NormalizedChannels)
            if (!TryNormalizeSlug(c, out _)) throw new ArgumentException($"Subtitles:Channels contains '{c}' — slugs are lowercase letters, digits, '_' and '-'.");
        if (BacklogSeconds is < 0 or > 3600) throw new ArgumentException("Subtitles:BacklogSeconds must be between 0 and 3600.");
        if (MaxBacklogItems is < 0 or > 1000) throw new ArgumentException("Subtitles:MaxBacklogItems must be between 0 and 1000.");
        if (KeepAliveSeconds is < 1 or > 120) throw new ArgumentException("Subtitles:KeepAliveSeconds must be between 1 and 120.");
    }

    private static string Sanitize(string s) =>
        new(s.ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').Take(24).ToArray());

    [GeneratedRegex("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
