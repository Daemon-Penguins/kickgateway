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
    /// translation that arrives minutes late is useless for captions and would only burn provider quota.
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

    public ProviderOptions Provider { get; set; } = new();

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
        Provider.Validate();
    }

    private static bool IsLanguageCode(string code) => code.Length is 2 or 3 && code.All(char.IsAsciiLetterLower);

    private static string Sanitize(string s) =>
        new(s.ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').Take(24).ToArray());
}

/// <summary>The translation provider (section <c>Translator:Provider</c>).</summary>
public sealed class ProviderOptions
{
    /// <summary>
    /// <c>deepl</c> (default — DeepL API, 500k characters/month on the free plan, best de→pl quality, ~0.5 s),
    /// <c>openai</c> (any <c>/chat/completions</c>-compatible endpoint: a local Ollama/LM Studio at no cost, LiteLLM, OpenRouter, OpenAI),
    /// <c>anthropic</c> (Messages API) or <c>stub</c> (no network, marks text — for wiring tests only).
    /// </summary>
    public string Name { get; set; } = "deepl";

    /// <summary>
    /// API base URL. Defaults: DeepL <c>https://api-free.deepl.com</c> for a free key (ends with <c>:fx</c>) else
    /// <c>https://api.deepl.com</c>; OpenAI <c>https://api.openai.com/v1</c> (Ollama: <c>http://host:11434/v1</c>);
    /// Anthropic <c>https://api.anthropic.com</c>.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Required for DeepL and Anthropic; optional for <c>openai</c> (a local Ollama needs none).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model id for the LLM providers. Defaults: OpenAI-compatible <c>gpt-4o-mini</c>, Anthropic <c>claude-haiku-5-5</c>. Ignored by DeepL.</summary>
    public string? Model { get; set; }

    /// <summary>Whole call budget; a slice that misses it is simply left untranslated.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>LLM providers only.</summary>
    public int MaxOutputTokens { get; set; } = 1000;

    /// <summary>LLM providers only: replaces the built-in system prompt when set. <c>{source}</c> / <c>{target}</c> are substituted.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>DeepL only: <c>default</c>, <c>more</c>, <c>less</c>, <c>prefer_more</c>, <c>prefer_less</c> (default — stream speech is casual).</summary>
    public string Formality { get; set; } = "prefer_less";

    public string NormalizedName => (Name ?? "").Trim().ToLowerInvariant();

    public bool IsDeepLFreeKey => (ApiKey ?? "").Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase);

    public string EffectiveBaseUrl => !string.IsNullOrWhiteSpace(BaseUrl)
        ? BaseUrl.Trim().TrimEnd('/')
        : NormalizedName switch
        {
            "deepl" => IsDeepLFreeKey ? "https://api-free.deepl.com" : "https://api.deepl.com",
            "openai" => "https://api.openai.com/v1",
            _ => "https://api.anthropic.com",
        };

    public string EffectiveModel => !string.IsNullOrWhiteSpace(Model)
        ? Model.Trim()
        : NormalizedName == "openai" ? "gpt-4o-mini" : "claude-haiku-5-5";

    public void Validate()
    {
        if (NormalizedName is not ("deepl" or "openai" or "anthropic" or "stub"))
            throw new ArgumentException("Translator:Provider:Name must be 'deepl', 'openai', 'anthropic' or 'stub'.");
        if (NormalizedName is "deepl" or "anthropic" && string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException($"Translator:Provider:ApiKey is required for {NormalizedName} (or use Name=stub for a dry run).");
        if (TimeoutSeconds is < 3 or > 120) throw new ArgumentException("Translator:Provider:TimeoutSeconds must be between 3 and 120.");
        if (MaxOutputTokens is < 100 or > 8000) throw new ArgumentException("Translator:Provider:MaxOutputTokens must be between 100 and 8000.");
        if ((Formality ?? "").Trim().ToLowerInvariant() is not ("default" or "more" or "less" or "prefer_more" or "prefer_less"))
            throw new ArgumentException("Translator:Provider:Formality must be default, more, less, prefer_more or prefer_less.");
    }
}
