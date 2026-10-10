using System.Diagnostics;
using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.Translator.Translation;

namespace TailoredApps.KickGateway.Subscribers.Translator.Consumers;

/// <summary>
/// One transcript in → one provider call → one <see cref="LiveTranscriptTranslation"/> out (published
/// directly — this service has no database, so no outbox). Segments go to the model as numbered lines
/// and come back aligned, keeping the original timings; when the model breaks the numbering the whole
/// translation becomes a single segment spanning the slice. Failures are logged and the slice skipped.
/// </summary>
public sealed class LiveTranscriptConsumer(
    TranslatorOptions opts,
    ITranslator translator,
    ILogger<LiveTranscriptConsumer> log) : IConsumer<LiveTranscript>
{
    public async Task Consume(ConsumeContext<LiveTranscript> context)
    {
        var t = context.Message;
        var slug = (t.BroadcasterSlug ?? "").Trim().ToLowerInvariant();

        if (TranslationPolicy.SkipReason(t, opts, DateTime.UtcNow) is { } reason)
        {
            log.LogDebug("[{Slug}] skip ({Reason}): [{Lang}] {Text}", slug, reason, t.Language, Preview(t.Text));
            return;
        }

        var source = t.Language.Trim().ToLowerInvariant();
        var target = opts.NormalizedTargetLanguage;
        var segments = (t.Segments ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToArray();
        if (segments.Length == 0)
            segments = [new LiveTranscriptSegment { StartedAt = t.StartedAt, EndedAt = t.EndedAt, Text = t.Text, Confidence = t.Confidence }];

        var sw = Stopwatch.StartNew();
        string output;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(opts.Llm.TimeoutSeconds));
            output = await translator.TranslateAsync(
                TranslationPrompt.SystemPrompt(source, target, opts.Llm.SystemPrompt),
                TranslationPrompt.NumberedLines(segments.Select(s => s.Text).ToArray()),
                cts.Token);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            log.LogWarning("[{Slug}] translation timed out after {Sec}s — slice left untranslated", slug, opts.Llm.TimeoutSeconds);
            return;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[{Slug}] translation failed — slice left untranslated", slug);
            return;
        }
        sw.Stop();

        var aligned = TranslationPrompt.ParseNumbered(output, segments.Length);
        LiveTranscriptSegment[] translated;
        if (aligned is not null)
        {
            translated = segments.Select((s, i) => new LiveTranscriptSegment { StartedAt = s.StartedAt, EndedAt = s.EndedAt, Text = aligned[i], Confidence = s.Confidence }).ToArray();
        }
        else
        {
            var whole = TranslationPrompt.Unnumbered(output);
            if (whole.Length == 0)
            {
                log.LogWarning("[{Slug}] provider returned nothing usable — slice left untranslated", slug);
                return;
            }
            log.LogDebug("[{Slug}] provider broke the numbering ({N} segments) — using one segment for the slice", slug, segments.Length);
            translated = [new LiveTranscriptSegment { StartedAt = t.StartedAt, EndedAt = t.EndedAt, Text = whole, Confidence = t.Confidence }];
        }

        var message = new LiveTranscriptTranslation
        {
            BroadcasterSlug = slug,
            KickChannelId = t.KickChannelId ?? "",
            StartedAt = t.StartedAt,
            EndedAt = t.EndedAt,
            AudioStartSeconds = t.AudioStartSeconds,
            SourceLanguage = source,
            TargetLanguage = target,
            Text = string.Join(' ', translated.Select(s => s.Text)),
            Segments = translated,
            Provider = translator.Name,
            TranslatedAt = DateTime.UtcNow,
            ProcessingSeconds = sw.Elapsed.TotalSeconds,
        };
        await context.Publish(message, context.CancellationToken);

        log.LogInformation("[{Slug}] {Src}→{Dst} in {Ms} ms{Aligned}: {Text}", slug, source, target, sw.ElapsedMilliseconds,
            aligned is null ? " (unaligned)" : "", Preview(message.Text));
    }

    private static string Preview(string? text) => text is null ? "" : text.Length <= 120 ? text : text[..117] + "...";
}
