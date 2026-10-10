using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TailoredApps.KickGateway.Subscribers.Translator.Translation;

/// <summary>
/// The provider-independent part of a translation call: the system prompt, the numbered-lines input
/// (one line per Whisper segment, so timings survive) and the parser that maps the numbered output
/// back onto the segments. Pure.
/// </summary>
public static partial class TranslationPrompt
{
    private const string DefaultSystemPrompt =
        "You translate live-stream speech transcripts from {source} into {target}. " +
        "The input is numbered lines; each line is one segment of spoken language as a speech recognizer wrote it: " +
        "colloquial, often half sentences, with slang, profanity and recognition errors. " +
        "Output exactly the same number of numbered lines, each line being the translation of the line with the same number, " +
        "and nothing else: no notes, no quotes, no explanations. Keep the register (casual stays casual, profanity stays profanity, never censor), " +
        "keep names, nicknames and numbers unchanged, do not add or drop content. " +
        "If a line is noise, a sound tag or not translatable, output it unchanged.";

    public static string SystemPrompt(string source, string target, string? custom = null) =>
        (string.IsNullOrWhiteSpace(custom) ? DefaultSystemPrompt : custom)
            .Replace("{source}", LanguageName(source))
            .Replace("{target}", LanguageName(target));

    /// <summary>"1: first segment\n2: second segment…"</summary>
    public static string NumberedLines(IReadOnlyList<string> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
            sb.Append(i + 1).Append(": ").Append(OneLine(segments[i])).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Maps the provider's numbered output back onto <paramref name="count"/> segments. Returns null when the
    /// output can't be aligned (wrong count, no numbering) — the caller then falls back to a single segment.
    /// </summary>
    public static string[]? ParseNumbered(string? output, int count)
    {
        if (string.IsNullOrWhiteSpace(output) || count <= 0) return null;
        var byNumber = new Dictionary<int, string>();
        foreach (var raw in output.Split('\n'))
        {
            var m = NumberedLine().Match(raw.Trim());
            if (!m.Success) continue;
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (n < 1 || n > count || byNumber.ContainsKey(n)) continue;
            byNumber[n] = m.Groups[2].Value.Trim();
        }
        if (byNumber.Count != count) return null;
        var result = new string[count];
        for (var i = 0; i < count; i++) result[i] = byNumber[i + 1];
        return result;
    }

    /// <summary>Everything the provider wrote, with any numbering stripped — the whole-slice fallback.</summary>
    public static string Unnumbered(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        var parts = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => NumberedLine().Match(l) is { Success: true } m ? m.Groups[2].Value.Trim() : l);
        return string.Join(" ", parts).Trim();
    }

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "pl" => "Polish",
        "de" => "German",
        "en" => "English",
        "uk" => "Ukrainian",
        "ru" => "Russian",
        "cs" => "Czech",
        "sk" => "Slovak",
        "es" => "Spanish",
        "fr" => "French",
        "it" => "Italian",
        "pt" => "Portuguese",
        "nl" => "Dutch",
        "tr" => "Turkish",
        _ => code,
    };

    [GeneratedRegex(@"^(\d{1,3})\s*[:.)\-]\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedLine();
}
