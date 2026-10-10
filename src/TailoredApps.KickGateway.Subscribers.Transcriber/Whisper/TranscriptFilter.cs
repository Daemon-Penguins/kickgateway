using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;

/// <summary>
/// Decides whether a Whisper segment is real speech worth publishing. Whisper hallucinates on
/// silence and music — subtitle credits, "thanks for watching", a word repeated twenty times,
/// bracketed sound tags — so segments are dropped when they are blank, non-speech tags only,
/// essentially one of the configured suppress phrases, an echo of the profanity prompt, degenerate
/// repetition, or below the confidence floor. Pure.
/// </summary>
public sealed partial class TranscriptFilter
{
    private readonly float _minConfidence;
    private readonly string[] _suppress;
    private readonly HashSet<string> _promptWords;

    /// <param name="promptWords">
    /// Words of the initial prompt Whisper is known to read back on noise (the profanity list). A segment that
    /// is mostly these words, or repeats one of them three times, is a prompt echo, not speech.
    /// </param>
    public TranscriptFilter(float minConfidence, IEnumerable<string> suppressPhrases, IEnumerable<string>? promptWords = null)
    {
        _minConfidence = minConfidence;
        _suppress = suppressPhrases
            .Select(Normalize)
            .Where(p => p.Length > 0)
            .Distinct()
            .ToArray();
        _promptWords = (promptWords ?? [])
            .SelectMany(w => Normalize(w).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Returns true when the segment should be kept; otherwise <paramref name="reason"/> says why it was dropped.</summary>
    public bool Accept(string? text, float probability, out string? reason)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 || !trimmed.Any(char.IsLetterOrDigit))
        {
            reason = "blank";
            return false;
        }

        var withoutTags = NonSpeechTags().Replace(trimmed, " ");
        if (!withoutTags.Any(char.IsLetterOrDigit))
        {
            reason = "non-speech";
            return false;
        }

        var normalized = Normalize(withoutTags);
        foreach (var phrase in _suppress)
        {
            // Equal, or the phrase makes up most of a short segment ("Dziękuję za oglądanie!" + a stray word).
            if (normalized == phrase || (normalized.Contains(phrase) && normalized.Length <= phrase.Length * 2))
            {
                reason = "suppressed-phrase";
                return false;
            }
        }

        if (IsPromptEcho(normalized))
        {
            reason = "prompt-echo";
            return false;
        }

        if (IsRepetitive(normalized))
        {
            reason = "repetitive";
            return false;
        }

        if (_minConfidence > 0 && probability > 0 && probability < _minConfidence)
        {
            reason = "low-confidence";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Chunk-level loop check. Whisper's other failure mode is emitting the same short sentence as
    /// several consecutive segments — each one passes <see cref="Accept"/> on its own. A run of at
    /// least <paramref name="minRun"/> identical (normalized) consecutive segments is a loop and
    /// is removed entirely; saying something twice is still allowed.
    /// </summary>
    public static List<T> DropLoops<T>(IReadOnlyList<T> segments, Func<T, string> text, int minRun = 3, Action<int, string>? onDropped = null)
    {
        var kept = new List<T>(segments.Count);
        var i = 0;
        while (i < segments.Count)
        {
            var key = Normalize(text(segments[i]));
            var j = i + 1;
            while (j < segments.Count && Normalize(text(segments[j])) == key) j++;
            var run = j - i;
            if (run >= minRun && key.Length > 0)
                onDropped?.Invoke(run, text(segments[i]));
            else
                for (var k = i; k < j; k++) kept.Add(segments[k]);
            i = j;
        }
        return kept;
    }

    /// <summary>
    /// Lowercase, diacritics folded to base letters (ę→e, ł→l), letters/digits only, single spaces —
    /// so matching ignores punctuation, casing and Whisper's inconsistent Polish spelling
    /// ("Dziękuje za oglądanie" must still hit "Dziękuję za oglądanie").
    /// </summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var raw in s.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue; // combining accents
            var ch = raw switch { 'ł' => 'l', 'ø' => 'o', 'ß' => 's', 'đ' => 'd', _ => raw }; // letters that don't decompose
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Seeded by the profanity prompt, Whisper fills music/noise with the prompt itself: the same prompt
    /// word three or more times ("pojebane, pojebane, pojebane"), or a segment of at least four words
    /// that is at least half prompt vocabulary ("Kurwa, chuj, pierdolic, jebac, zajebiscie"). A swear
    /// word or two inside a real sentence passes.
    /// </summary>
    public bool IsPromptEcho(string normalized)
    {
        if (_promptWords.Count == 0) return false;
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = words.Where(_promptWords.Contains).ToList();
        if (hits.Count == 0) return false;
        if (hits.GroupBy(w => w).Any(g => g.Count() >= 3)) return true;
        return words.Length >= 4 && hits.Count * 2 >= words.Length;
    }

    /// <summary>
    /// Whisper's failure mode on noise is a loop: one or two tokens repeated for the whole window.
    /// Eight or more words with almost no distinct ones is never real speech.
    /// </summary>
    public static bool IsRepetitive(string normalized)
    {
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 8) return false;
        var distinct = words.Distinct().Count();
        return distinct <= Math.Max(1, words.Length / 4);
    }

    // [muzyka], (śmiech), *laughs*, ♪ … — sound/event tags Whisper emits instead of words.
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[♪♫♬]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSpeechTags();
}
