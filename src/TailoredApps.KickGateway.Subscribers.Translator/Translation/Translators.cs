using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TailoredApps.KickGateway.Subscribers.Translator.Translation;

/// <summary>
/// DeepL API (<c>POST /v2/translate</c>). The segment texts go as the <c>text</c> array and come back one for
/// one, so alignment is guaranteed; the whole slice is passed as <c>context</c> so each short line is
/// translated knowing its neighbours. Free keys (<c>…:fx</c>) use the free endpoint. Quota exhausted = 456.
/// </summary>
public sealed class DeepLTranslator(HttpClient http, ProviderOptions opts) : ITranslator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Name => "deepl/" + (opts.IsDeepLFreeKey ? "free" : "pro");

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, opts.EffectiveBaseUrl + "/v2/translate");
        req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", opts.ApiKey!.Trim());
        var formality = (opts.Formality ?? "prefer_less").Trim().ToLowerInvariant();
        req.Content = JsonContent.Create(new
        {
            text = request.Lines.ToArray(),
            source_lang = request.SourceLanguage.ToUpperInvariant(),
            target_lang = request.TargetLanguage.ToUpperInvariant(),
            context = string.Join(" ", request.Lines),
            preserve_formatting = true,
            formality = formality == "default" ? null : formality,
        }, options: Json);

        using var res = await ProviderHttp.SendWithRetryAsync(http, req, ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var translations = doc.RootElement.GetProperty("translations").EnumerateArray()
            .Select(t => t.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "")
            .ToArray();
        return translations.Length == request.Lines.Count
            ? TranslationResult.FromAligned(translations)
            : TranslationResult.FromWhole(string.Join(" ", translations));
    }
}

/// <summary>Any OpenAI-compatible <c>/chat/completions</c> endpoint: a local Ollama / LM Studio (no key), LiteLLM, OpenRouter, OpenAI, …</summary>
public sealed class OpenAiCompatibleTranslator(HttpClient http, ProviderOptions opts) : ITranslator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Name => "openai/" + opts.EffectiveModel;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, opts.EffectiveBaseUrl + "/chat/completions");
        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey.Trim());
        req.Content = JsonContent.Create(new
        {
            model = opts.EffectiveModel,
            max_tokens = opts.MaxOutputTokens,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = TranslationPrompt.SystemPrompt(request.SourceLanguage, request.TargetLanguage, opts.SystemPrompt) },
                new { role = "user", content = TranslationPrompt.NumberedLines(request.Lines) },
            },
        }, options: Json);

        using var res = await ProviderHttp.SendWithRetryAsync(http, req, ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var output = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return TranslationPrompt.ToResult(output, request.Lines.Count);
    }
}

/// <summary>Anthropic Messages API (<c>POST /v1/messages</c>).</summary>
public sealed class AnthropicTranslator(HttpClient http, ProviderOptions opts) : ITranslator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Name => "anthropic/" + opts.EffectiveModel;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, opts.EffectiveBaseUrl + "/v1/messages");
        req.Headers.Add("x-api-key", opts.ApiKey!.Trim());
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = JsonContent.Create(new
        {
            model = opts.EffectiveModel,
            max_tokens = opts.MaxOutputTokens,
            temperature = 0.2,
            system = TranslationPrompt.SystemPrompt(request.SourceLanguage, request.TargetLanguage, opts.SystemPrompt),
            messages = new[] { new { role = "user", content = TranslationPrompt.NumberedLines(request.Lines) } },
        }, options: Json);

        using var res = await ProviderHttp.SendWithRetryAsync(http, req, ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var sb = new System.Text.StringBuilder();
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        return TranslationPrompt.ToResult(sb.ToString(), request.Lines.Count);
    }
}

/// <summary>No network: marks every line so the whole pipeline (bus → page) can be exercised without a provider.</summary>
public sealed class StubTranslator(ProviderOptions opts) : ITranslator
{
    public string Name => "stub";

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct) =>
        Task.FromResult(TranslationResult.FromAligned(request.Lines.Select(l => $"[{opts.Model ?? "stub"}] {l.Trim()}").ToArray()));
}

internal static class ProviderHttp
{
    /// <summary>One retry on 429/5xx/transport errors, with a short pause; anything else surfaces (DeepL 456 = quota exhausted).</summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? res = null;
            HttpRequestException? transport = null;
            try
            {
                res = await http.SendAsync(attempt == 1 ? req : Clone(req), HttpCompletionOption.ResponseContentRead, ct);
            }
            catch (HttpRequestException ex)
            {
                transport = ex; // DNS / connection / TLS - worth one more try
            }

            if (res is not null)
            {
                if (res.IsSuccessStatusCode) return res;
                var retryable = res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500;
                if (attempt >= 2 || !retryable)
                {
                    var body = await res.Content.ReadAsStringAsync(ct);
                    var hint = (int)res.StatusCode == 456 ? " (DeepL: character quota exhausted)" : "";
                    var message = $"{(int)res.StatusCode} {res.ReasonPhrase}{hint}: {Trim(body)}";
                    res.Dispose();
                    throw new HttpRequestException(message);
                }
                res.Dispose();
            }
            else if (attempt >= 2)
            {
                throw transport!;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Content = req.Content };
        foreach (var h in req.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
