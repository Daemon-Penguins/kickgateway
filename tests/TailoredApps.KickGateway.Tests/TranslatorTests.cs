using System.Net;
using System.Text;
using System.Text.Json;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.Translator;
using TailoredApps.KickGateway.Subscribers.Translator.Translation;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// The translator's pure parts and its provider adapters against canned HTTP: which slices get a call,
/// how segments go out and come back aligned, and the configuration rules.
/// </summary>
public class TranslatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 16, 0, 0, DateTimeKind.Utc);

    private static TranslatorOptions Options() => new()
    {
        SourceLanguages = "de, EN",
        TargetLanguage = "pl",
        MinConfidence = 0.45f,
        MaxAgeSeconds = 120,
        Provider = new ProviderOptions { Name = "stub" },
    };

    private static LiveTranscript Transcript(string language = "de", string text = "Guten Abend", float confidence = 0.8f, DateTime? transcribedAt = null, string slug = "chef_jan") => new()
    {
        BroadcasterSlug = slug,
        StartedAt = T0,
        EndedAt = T0.AddSeconds(12),
        AudioStartSeconds = 30,
        Text = text,
        Language = language,
        Confidence = confidence,
        TranscribedAt = transcribedAt ?? T0.AddSeconds(13),
        Segments = [new LiveTranscriptSegment { StartedAt = T0, EndedAt = T0.AddSeconds(12), Text = text, Confidence = confidence }],
    };

    /// <summary>Answers every request with one canned body and records what was sent.</summary>
    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    // ---- policy ----

    [Fact]
    public void German_slice_is_translated_and_the_rest_is_skipped_for_a_reason()
    {
        var o = Options();
        var now = T0.AddSeconds(20);
        Assert.Null(TranslationPolicy.SkipReason(Transcript("de"), o, now));
        Assert.Null(TranslationPolicy.SkipReason(Transcript("EN"), o, now));                    // case-insensitive source list
        Assert.Equal("already-target", TranslationPolicy.SkipReason(Transcript("pl"), o, now));
        Assert.Equal("language-not-configured", TranslationPolicy.SkipReason(Transcript("fr"), o, now));
        Assert.Equal("no-language", TranslationPolicy.SkipReason(Transcript(""), o, now));
        Assert.Equal("empty", TranslationPolicy.SkipReason(Transcript("de", text: "  "), o, now));
        Assert.Equal("low-confidence", TranslationPolicy.SkipReason(Transcript("de", confidence: 0.3f), o, now));
        Assert.Null(TranslationPolicy.SkipReason(Transcript("de", confidence: 0f), o, now));     // 0 = unknown, never a reason
        Assert.Equal("stale", TranslationPolicy.SkipReason(Transcript("de", transcribedAt: T0), o, T0.AddSeconds(121)));
    }

    [Fact]
    public void Channel_allowlist_is_applied_when_set()
    {
        var o = Options();
        o.Channels = "chef_jan, Trazer";
        Assert.Null(TranslationPolicy.SkipReason(Transcript("de", slug: "Chef_Jan"), o, T0));
        Assert.Equal("channel-not-configured", TranslationPolicy.SkipReason(Transcript("de", slug: "azdus"), o, T0));
        Assert.Equal(["chef_jan", "trazer"], o.NormalizedChannels);
    }

    // ---- LLM prompt / parsing ----

    [Fact]
    public void Segments_become_numbered_lines_and_newlines_are_flattened()
    {
        var lines = TranslationPrompt.NumberedLines(["Guten Abend", "wie\ngeht's", " alles klar "]);
        Assert.Equal("1: Guten Abend\n2: wie geht's\n3: alles klar\n", lines);
    }

    [Fact]
    public void System_prompt_names_the_languages_and_can_be_overridden()
    {
        var prompt = TranslationPrompt.SystemPrompt("de", "pl");
        Assert.Contains("from German into Polish", prompt);
        Assert.Contains("never censor", prompt);
        Assert.Equal("X German→Polish", TranslationPrompt.SystemPrompt("de", "pl", "X {source}→{target}"));
    }

    [Theory]
    [InlineData("1: Dobry wieczór\n2: jak leci\n3: wszystko jasne")]
    [InlineData("1. Dobry wieczór\n2) jak leci\n3 - wszystko jasne\n")]
    [InlineData("Here you go:\n\n1: Dobry wieczór\n  2: jak leci  \n3: wszystko jasne\n\nLet me know if you need more.")]
    public void Numbered_output_is_aligned_back_onto_the_segments(string output)
    {
        var result = TranslationPrompt.ToResult(output, 3);
        Assert.NotNull(result.Aligned);
        Assert.Equal(["Dobry wieczór", "jak leci", "wszystko jasne"], result.Aligned);
        Assert.Equal("Dobry wieczór jak leci wszystko jasne", result.Whole);
    }

    [Theory]
    [InlineData("1: Dobry wieczór\n2: jak leci")]                       // one line short
    [InlineData("1: Dobry wieczór\n2: jak leci\n4: wszystko jasne")]   // wrong number
    [InlineData("Dobry wieczór, jak leci, wszystko jasne")]           // no numbering
    public void Broken_numbering_is_not_aligned_and_falls_back_to_the_whole_text(string output)
    {
        var result = TranslationPrompt.ToResult(output, 3);
        Assert.Null(result.Aligned);
        Assert.Contains("Dobry", result.Whole);
        Assert.DoesNotContain("1:", result.Whole);
        Assert.False(result.IsEmpty);
        Assert.True(TranslationPrompt.ToResult("", 3).IsEmpty);
    }

    // ---- providers against canned HTTP ----

    [Fact]
    public async Task DeepL_sends_the_lines_as_an_array_with_context_and_maps_them_back_one_for_one()
    {
        var handler = new CannedHandler(HttpStatusCode.OK,
            """{"translations":[{"detected_source_language":"DE","text":"Dobry wieczór"},{"detected_source_language":"DE","text":"jak leci"}]}""");
        var opts = new ProviderOptions { Name = "deepl", ApiKey = "abc:fx" };
        var deepl = new DeepLTranslator(new HttpClient(handler), opts);

        var result = await deepl.TranslateAsync(new TranslationRequest("de", "pl", ["Guten Abend", "wie geht's"]), default);

        Assert.Equal("deepl/free", deepl.Name);
        Assert.Equal("https://api-free.deepl.com/v2/translate", handler.Last!.RequestUri!.ToString());
        Assert.Equal("DeepL-Auth-Key", handler.Last.Headers.Authorization!.Scheme);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(2, sent.RootElement.GetProperty("text").GetArrayLength());
        Assert.Equal("DE", sent.RootElement.GetProperty("source_lang").GetString());
        Assert.Equal("PL", sent.RootElement.GetProperty("target_lang").GetString());
        Assert.Equal("Guten Abend wie geht's", sent.RootElement.GetProperty("context").GetString());
        Assert.Equal("prefer_less", sent.RootElement.GetProperty("formality").GetString());
        Assert.Equal(["Dobry wieczór", "jak leci"], result.Aligned);
    }

    [Fact]
    public async Task DeepL_pro_key_uses_the_paid_endpoint_and_quota_exhaustion_surfaces()
    {
        var ok = new ProviderOptions { Name = "deepl", ApiKey = "abc" };
        Assert.Equal("https://api.deepl.com", ok.EffectiveBaseUrl);
        Assert.Equal("deepl/pro", new DeepLTranslator(new HttpClient(new CannedHandler(HttpStatusCode.OK, "{}")), ok).Name);

        var quota = new CannedHandler((HttpStatusCode)456, """{"message":"Quota Exceeded"}""");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new DeepLTranslator(new HttpClient(quota), ok).TranslateAsync(new TranslationRequest("de", "pl", ["x"]), default));
        Assert.Contains("quota exhausted", ex.Message);
        Assert.Equal(1, quota.Calls); // 456 is not retried
    }

    [Fact]
    public async Task OpenAi_compatible_provider_works_without_a_key_for_a_local_model_and_parses_the_numbered_answer()
    {
        var handler = new CannedHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"1: Dobry wieczór\n2: jak leci"}}]}""");
        var opts = new ProviderOptions { Name = "openai", BaseUrl = "http://mac-mini:11434/v1/", Model = "qwen2.5:7b" };
        opts.Validate(); // no key needed
        var ollama = new OpenAiCompatibleTranslator(new HttpClient(handler), opts);

        var result = await ollama.TranslateAsync(new TranslationRequest("de", "pl", ["Guten Abend", "wie geht's"]), default);

        Assert.Equal("openai/qwen2.5:7b", ollama.Name);
        Assert.Equal("http://mac-mini:11434/v1/chat/completions", handler.Last!.RequestUri!.ToString());
        Assert.Null(handler.Last.Headers.Authorization);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("qwen2.5:7b", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("1: Guten Abend\n2: wie geht's\n", sent.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(["Dobry wieczór", "jak leci"], result.Aligned);
    }

    [Fact]
    public async Task Anthropic_provider_sends_the_messages_shape_and_retries_once_on_5xx()
    {
        var handler = new CannedHandler(HttpStatusCode.BadGateway, "upstream");
        var opts = new ProviderOptions { Name = "anthropic", ApiKey = "k" };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new AnthropicTranslator(new HttpClient(handler), opts).TranslateAsync(new TranslationRequest("de", "pl", ["x"]), default));
        Assert.Contains("502", ex.Message);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Last!.RequestUri!.ToString());
        Assert.Contains("x-api-key", handler.Last.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task Stub_provider_marks_every_line_and_stays_aligned()
    {
        var stub = new StubTranslator(new ProviderOptions { Name = "stub", Model = "test" });
        var result = await stub.TranslateAsync(new TranslationRequest("de", "pl", ["Guten Abend", "wie geht's"]), default);
        Assert.Equal(["[test] Guten Abend", "[test] wie geht's"], result.Aligned);
        Assert.Equal("stub", stub.Name);
    }

    // ---- options ----

    [Fact]
    public void Options_validate_languages_and_provider()
    {
        Options().Validate();
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { SourceLanguages = "", Provider = new ProviderOptions { Name = "stub" } }.Validate());
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { SourceLanguages = "de", TargetLanguage = "de", Provider = new ProviderOptions { Name = "stub" } }.Validate());
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { Provider = new ProviderOptions { Name = "deepl", ApiKey = "" } }.Validate());      // key required
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { Provider = new ProviderOptions { Name = "anthropic", ApiKey = "" } }.Validate());
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { Provider = new ProviderOptions { Name = "google", ApiKey = "x" } }.Validate());
        Assert.Throws<ArgumentException>(() => new TranslatorOptions { Provider = new ProviderOptions { Name = "deepl", ApiKey = "x", Formality = "casual" } }.Validate());

        Assert.Equal("deepl", new ProviderOptions().NormalizedName); // the default: free quota, best de→pl
        var anthropic = new ProviderOptions { Name = "Anthropic", ApiKey = "k" };
        Assert.Equal("https://api.anthropic.com", anthropic.EffectiveBaseUrl);
        Assert.Equal("claude-haiku-5-5", anthropic.EffectiveModel);
        var openai = new ProviderOptions { Name = "openai", BaseUrl = "http://litellm:4000/v1/" };
        Assert.Equal("http://litellm:4000/v1", openai.EffectiveBaseUrl);
        Assert.Equal("gpt-4o-mini", openai.EffectiveModel);
    }

    [Fact]
    public void Queue_name_is_unique_per_process_unless_configured()
    {
        Assert.StartsWith("translator-", new TranslatorOptions().EffectiveQueueName);
        Assert.NotEqual(new TranslatorOptions().EffectiveQueueName, new TranslatorOptions().EffectiveQueueName);
        Assert.Equal("fixed", new TranslatorOptions { QueueName = " fixed " }.EffectiveQueueName);
    }
}
