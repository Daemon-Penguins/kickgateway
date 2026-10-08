using System.Text.RegularExpressions;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>Term/emote with its frequency.</summary>
public sealed record TermCount(string Term, int Count);

/// <summary>How a chatter writes. Shares are fractions of <see cref="Messages"/> (0..1).</summary>
public sealed record ChatStyle(
    int Messages,
    double AvgLength,
    double EmoteShare,
    double EmoteOnlyShare,
    double CommandShare,
    double QuestionShare,
    double LinkShare,
    double CapsShare,
    IReadOnlyList<TermCount> TopTerms,
    IReadOnlyList<TermCount> TopEmotes);

/// <summary>
/// Cheap, language-agnostic text statistics over chat messages. Deliberately shallow — tone,
/// topics and sentiment are left to whoever reads the sample messages (the LLM on the MCP side).
/// </summary>
public static partial class ChatTextStats
{
    [GeneratedRegex(@"\[emote:(\d+):([^\]]*)\]")]
    private static partial Regex EmoteRegex();

    [GeneratedRegex(@"(?:https?://|www\.)\S*", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    // Chat filler in the two languages this gateway mostly sees (English + Polish). Not a
    // linguistic stopword list — just enough to keep "the/and/nie/jak" out of top terms.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "you", "for", "that", "this", "with", "are", "was", "but", "not", "have", "has",
        "just", "what", "all", "can", "its", "it's", "his", "her", "they", "them", "your", "from",
        "too", "out", "get", "got", "one", "how", "why", "who", "when", "then", "than", "there",
        "here", "yes", "yeah", "lol", "im", "i'm", "dont", "don't", "will", "would", "like", "now",
        "nie", "jak", "jest", "to", "się", "sie", "czy", "ale", "tak", "już", "juz", "tylko", "jeszcze",
        "coś", "cos", "tego", "tej", "ten", "ta", "być", "byc", "był", "byl", "było", "bylo", "mnie",
        "mam", "masz", "ma", "go", "jej", "ich", "bo", "że", "ze", "no", "co", "na", "do", "od", "po",
        "za", "przez", "dla", "też", "tez", "wiem", "jako", "kto", "gdzie", "tam", "tu", "tutaj",
    };

    public static ChatStyle Compute(IReadOnlyCollection<string> contents, int topTerms = 15, int topEmotes = 10)
    {
        if (contents.Count == 0)
            return new ChatStyle(0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<TermCount>(), Array.Empty<TermCount>());

        long totalLength = 0;
        int emote = 0, emoteOnly = 0, command = 0, question = 0, link = 0, caps = 0;
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        var emotes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in contents)
        {
            var content = raw ?? "";
            var emoteMatches = EmoteRegex().Matches(content);
            foreach (Match m in emoteMatches)
            {
                var name = m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value;
                emotes[name] = emotes.GetValueOrDefault(name) + 1;
            }

            // Measure/tokenise the text with emote tokens replaced by a single space.
            var text = emoteMatches.Count > 0 ? EmoteRegex().Replace(content, " ") : content;
            var trimmed = text.Trim();
            totalLength += trimmed.Length + emoteMatches.Count;

            if (emoteMatches.Count > 0) emote++;
            if (emoteMatches.Count > 0 && trimmed.Length == 0) emoteOnly++;
            if (trimmed.StartsWith('!')) command++;
            if (trimmed.Contains('?')) question++;
            if (LinkRegex().IsMatch(trimmed)) link++;
            if (IsShouting(trimmed)) caps++;

            if (trimmed.StartsWith('!')) continue; // bot commands aren't vocabulary
            foreach (var term in Tokenize(LinkRegex().Replace(trimmed, " ")))
                terms[term] = terms.GetValueOrDefault(term) + 1;
        }

        double n = contents.Count;
        return new ChatStyle(
            Messages: contents.Count,
            AvgLength: Math.Round(totalLength / n, 1),
            EmoteShare: Share(emote, n),
            EmoteOnlyShare: Share(emoteOnly, n),
            CommandShare: Share(command, n),
            QuestionShare: Share(question, n),
            LinkShare: Share(link, n),
            CapsShare: Share(caps, n),
            TopTerms: Top(terms, topTerms),
            TopEmotes: Top(emotes, topEmotes));
    }

    /// <summary>True when the message has ≥5 letters and ≥70% of them are uppercase.</summary>
    public static bool IsShouting(string text)
    {
        int letters = 0, upper = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (char.IsUpper(ch)) upper++;
        }
        return letters >= 5 && upper >= letters * 0.7;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var ch = i < text.Length ? text[i] : ' ';
            var wordChar = char.IsLetterOrDigit(ch) || ch == '\'' || (ch == '@' && start < 0);
            if (wordChar) { if (start < 0) start = i; continue; }
            if (start < 0) continue;

            var token = text[start..i].Trim('\'').ToLowerInvariant();
            start = -1;
            if (token.Length < 3 || token[0] == '@' || token.All(char.IsDigit) || Stopwords.Contains(token)) continue;
            yield return token;
        }
    }

    private static double Share(int count, double total) => total <= 0 ? 0 : Math.Round(count / total, 3);

    private static IReadOnlyList<TermCount> Top(Dictionary<string, int> counts, int take) =>
        counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(take).Select(kv => new TermCount(kv.Key, kv.Value)).ToList();
}
