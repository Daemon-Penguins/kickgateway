using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TailoredApps.KickGateway.Subscribers.Translator.Translation;

/// <summary>Anthropic Messages API (<c>POST /v1/messages</c>).</summary>
public sealed class AnthropicTranslator(HttpClient http, LlmOptions opts) : ITranslator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Name => "anthropic/" + opts.EffectiveModel;

    public async Task<string> TranslateAsync(string systemPrompt, string numberedLines, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, opts.EffectiveBaseUrl + "/v1/messages");
        req.Headers.Add("x-api-key", opts.ApiKey!.Trim());
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = JsonContent.Create(new
        {
            model = opts.EffectiveModel,
            max_tokens = opts.MaxOutputTokens,
            temperature = 0.2,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = numberedLines } },
        }, options: Json);

        using var res = await LlmHttp.SendWithRetryAsync(http, req, ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var sb = new System.Text.StringBuilder();
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        return sb.ToString();
    }
}

/// <summary>Any OpenAI-compatible <c>/chat/completions</c> endpoint: OpenAI, LiteLLM, OpenRouter, Ollama, …</summary>
public sealed class OpenAiCompatibleTranslator(HttpClient http, LlmOptions opts) : ITranslator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Name => "openai/" + opts.EffectiveModel;

    public async Task<string> TranslateAsync(string systemPrompt, string numberedLines, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, opts.EffectiveBaseUrl + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey!.Trim());
        req.Content = JsonContent.Create(new
        {
            model = opts.EffectiveModel,
            max_tokens = opts.MaxOutputTokens,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = numberedLines },
            },
        }, options: Json);

        using var res = await LlmHttp.SendWithRetryAsync(http, req, ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

/// <summary>No network: marks every line so the whole pipeline (bus → page) can be exercised without a provider.</summary>
public sealed class StubTranslator(LlmOptions opts) : ITranslator
{
    public string Name => "stub";

    public Task<string> TranslateAsync(string systemPrompt, string numberedLines, CancellationToken ct) =>
        Task.FromResult(string.Join('\n', numberedLines.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => { var i = l.IndexOf(':'); return i > 0 ? l[..(i + 1)] + " [" + opts.EffectiveModel + "] " + l[(i + 1)..].Trim() : l; })));
}

internal static class LlmHttp
{
    /// <summary>One retry on 429/5xx/transport errors, with a short pause; anything else surfaces.</summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? res = null;
            try
            {
                res = await http.SendAsync(Clone(req, attempt), HttpCompletionOption.ResponseContentRead, ct);
                if (res.IsSuccessStatusCode) return res;
                if (attempt >= 2 || !(res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500))
                {
                    var body = await res.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase}: {Trim(body)}");
                }
                res.Dispose();
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                res?.Dispose();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage req, int attempt)
    {
        if (attempt == 1) return req;
        var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Content = req.Content };
        foreach (var h in req.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
