namespace TailoredApps.KickGateway.Subscribers.Translator.Translation;

/// <summary>One slice to translate: its segment texts, in order.</summary>
public sealed record TranslationRequest(string SourceLanguage, string TargetLanguage, IReadOnlyList<string> Lines);

/// <summary>
/// What a provider returned. <see cref="Aligned"/> has one translation per input line (same order) when the
/// provider kept the lines apart; otherwise it is null and <see cref="Whole"/> carries the slice as one text.
/// </summary>
public sealed record TranslationResult(string[]? Aligned, string Whole)
{
    public static TranslationResult FromAligned(string[] lines) => new(lines, string.Join(' ', lines.Select(l => l.Trim()).Where(l => l.Length > 0)));
    public static TranslationResult FromWhole(string text) => new(null, text.Trim());
    public bool IsEmpty => Whole.Length == 0;
}

/// <summary>One provider call. Throws on failure (the consumer logs and skips the slice).</summary>
public interface ITranslator
{
    /// <summary>Provider/model label stamped on the published translation, e.g. <c>deepl/free</c> or <c>openai/qwen2.5:7b</c>.</summary>
    string Name { get; }

    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct);
}
