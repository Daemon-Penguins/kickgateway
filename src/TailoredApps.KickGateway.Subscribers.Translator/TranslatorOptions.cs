using System.Security.Cryptography;

namespace TailoredApps.KickGateway.Subscribers.Translator;

/// <summary>Configuration of the translator (section <c>Translator</c>).</summary>
public sealed class TranslatorOptions
{
    public const string Section = "Translator";

    /// <summary>Comma-separated channel slugs to translate. Empty = every channel on the transcript exchange.</summary>
    public string Channels { get; set; } = "";

    /// <summary>
    /// Receive queue. Default: a unique per-process name — the queue is non-durable and auto-delete, because a
    /// translation that arrives minutes late is useless for captions and would only burn provider calls.
    /// </summary>
    public string? QueueName { get; set; }

    /// <summary>Comma-separated ISO-639-1 codes of the transcript languages to translate (default: German).</summary>
    public string SourceLanguages { get; set; } = "de";

    /// <summary>ISO-639-1 code to translate into.</summary>
    public string TargetLanguage { get; set; } = "pl";

    /// <summary>Slices whose Whisper confidence is below this are skipped — noise and music hallucinations aren't worth a call.</summary>
    public float MinConfidence { get; set; } = 0.45f;

    /// <summary>Slices transcribed longer ago than this are skipped (a backlog after downtime must not become a bill).</summary>
    public int MaxAgeSeconds { get; set; } = 120;

    /// <summary>Provider calls in flight at once.</summary>
    public int MaxConcurrency { get; set; } = 2;

    public LlmOptions Llm { get; set; } = new();

    private string? _effectiveQueueName;

    public string EffectiveQueueName => _effectiveQueueName ??= string.IsNullOrWhiteSpace(QueueName)
        ? $"translator-{Sanitize(Environment.MachineName)}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}"
        : QueueName.Trim();

    public string[] NormalizedChannels => (Channels ?? "")
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.ToLowerInvariant()).Distinct().ToArray();

    public string[] NormalizedSourceLanguages => (SourceLanguages ?? "")
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(l => l.ToLowerInvariant()).Distinct().ToArray();

    public string NormalizedTargetLanguage => (TargetLanguage ?? "").Trim().ToLowerInvariant();

    public void Validate()
    {
        if (NormalizedSourceLanguages.Length == 0) throw new ArgumentException("Translator:SourceLanguages must list at least one ISO-639-1 code (e.g. de,en).");
        foreach (var l in NormalizedSourceLanguages)
            if (!IsLanguageCode(l)) throw new ArgumentException($"Translator:SourceLanguages contains '{l}' — use ISO-639-1 codes.");
        if (!IsLanguageCode(NormalizedTargetLanguage)) throw new ArgumentException("Translator:TargetLanguage must be an ISO-639-1 code (e.g. pl).");
        if (NormalizedSourceLanguages.Contains(NormalizedTargetLanguage)) throw new ArgumentException("Translator:TargetLanguage must not be one of the source languages.");
        if (MinConfidence is < 0 or > 1) throw new ArgumentException("Translator:MinConfidence must be within [0, 1].");
        if (MaxAgeSeconds is < 10 or > 3600) throw new ArgumentException("Translator:MaxAgeSeconds must be between 10 and 3600.");
        if (MaxConcurrency is < 1 or > 16) throw new ArgumentException("Translator:MaxConcurrency must be between 1 and 16.");
        Llm.Validate();
    }

    private static bool IsLanguageCode(string code) => code.Length is 2 or 3 && code.All(char.IsAsciiLetterLower);

    private static string Sanitize(string s) =>
        new(s.ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').Take(24).ToArray());
}

/// <summary>The provider behind the translation (section <c>Translator:Llm</c>).</summary>
public sealed class LlmOptions
{
    /// <summary><c>anthropic</c> (Messages API), <c>openai</c> (any <c>/chat/completions</c>-compatible endpoint: OpenAI, LiteLLM, OpenRouter, Ollama) or <c>stub</c> (no network, marks text — for wiring tests only).</summary>
    public string Provider { get; set; } = "anthropic";

    /// <summary>API base URL. Defaults: Anthropic <c>https://api.anthropic.com</c>, OpenAI <c>https://api.openai.com/v1</c>.</summary>
    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Model id. Default for Anthropic: <c>claude-haiku-5-5</c> (fast, cheap, good at colloquial text).</summary>
    public string? Model { get; set; }

    /// <summary>Whole call budget; a slice that misses it is simply left untranslated.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    public int MaxOutputTokens { get; set; } = 1000;

    /// <summary>Replaces the built-in system prompt when set. <c>{source}</c> / <c>{target}</c> are substituted.</summary>
    public string? SystemPrompt { get; set; }

    public string NormalizedProvider => (Provider ?? "").Trim().ToLowerInvariant();

    public string EffectiveBaseUrl => !string.IsNullOrWhiteSpace(BaseUrl)
        ? BaseUrl.Trim().TrimEnd('/')
        : NormalizedProvider == "openai" ? "https://api.openai.com/v1" : "https://api.anthropic.com";

    public string EffectiveModel => !string.IsNullOrWhiteSpace(Model)
        ? Model.Trim()
        : NormalizedProvider == "openai" ? "gpt-4o-mini" : "claude-haiku-5-5";

    public void Validate()
    {
        if (NormalizedProvider is not ("anthropic" or "openai" or "stub"))
            throw new ArgumentException("Translator:Llm:Provider must be 'anthropic', 'openai' or 'stub'.");
        if (NormalizedProvider != "stub" && string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("Translator:Llm:ApiKey is required (or use Provider=stub for a dry run).");
        if (TimeoutSeconds is < 3 or > 120) throw new ArgumentException("Translator:Llm:TimeoutSeconds must be between 3 and 120.");
        if (MaxOutputTokens is < 100 or > 8000) throw new ArgumentException("Translator:Llm:MaxOutputTokens must be between 100 and 8000.");
    }
}
