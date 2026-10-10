namespace TailoredApps.KickGateway.Subscribers.Translator.Translation;

/// <summary>One provider call: numbered segment texts in, the raw model output out.</summary>
public interface ITranslator
{
    /// <summary>Provider/model label stamped on the published translation, e.g. <c>anthropic/claude-haiku-5-5</c>.</summary>
    string Name { get; }

    /// <summary>Returns the model's raw text for <paramref name="numberedLines"/>, or throws (the consumer logs and skips the slice).</summary>
    Task<string> TranslateAsync(string systemPrompt, string numberedLines, CancellationToken ct);
}
