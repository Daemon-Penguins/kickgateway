using System.Diagnostics;
using MassTransit;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.Translator.Translation;

namespace TailoredApps.KickGateway.Subscribers.Translator.Consumers;

/// <summary>
/// One transcript in → one provider call → one <see cref="LiveTranscriptTranslation"/> out (published
/// directly — this service has no database, so no outbox). Segments go to the provider as separate lines
/// and come back aligned, keeping the original timings; when a provider can't keep them apart the whole
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
        TranslationResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(opts.Provider.TimeoutSeconds));
            result = await translator.TranslateAsync(new TranslationRequest(source, target, segments.Select(s => s.Text.Trim()).ToArray()), cts.Token);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            log.LogWarning("[{Slug}] translation timed out after {Sec}s — slice left untranslated", slug, opts.Provider.TimeoutSeconds);
            return;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[{Slug}] translation failed — slice left untranslated", slug);
            return;
        }
        sw.Stop();

        if (result.IsEmpty)
        {
            log.LogWarning("[{Slug}] provider returned nothing usable — slice left untranslated", slug);
            return;
        }

        LiveTranscriptSegment[] translated = result.Aligned is { } aligned
            ? segments.Select((s, i) => new LiveTranscriptSegment { StartedAt = s.StartedAt, EndedAt = s.EndedAt, Text = aligned[i].Trim(), Confidence = s.Confidence }).ToArray()
            : [new LiveTranscriptSegment { StartedAt = t.StartedAt, EndedAt = t.EndedAt, Text = result.Whole, Confidence = t.Confidence }];
        if (result.Aligned is null)
            log.LogDebug("[{Slug}] provider did not keep the {N} lines apart — using one segment for the slice", slug, segments.Length);

        var message = new LiveTranscriptTranslation
        {
            BroadcasterSlug = slug,
            KickChannelId = t.KickChannelId ?? "",
            StartedAt = t.StartedAt,
            EndedAt = t.EndedAt,
            AudioStartSeconds = t.AudioStartSeconds,
            SourceLanguage = source,
            TargetLanguage = target,
            Text = string.Join(' ', translated.Select(s => s.Text).Where(s => s.Length > 0)),
            Segments = translated,
            Provider = translator.Name,
            TranslatedAt = DateTime.UtcNow,
            ProcessingSeconds = sw.Elapsed.TotalSeconds,
        };
        await context.Publish(message, context.CancellationToken);

        log.LogInformation("[{Slug}] {Src}→{Dst} via {Provider} in {Ms} ms{Aligned}: {Text}", slug, source, target, translator.Name, sw.ElapsedMilliseconds,
            result.Aligned is null ? " (unaligned)" : "", Preview(message.Text));
    }

    private static string Preview(string? text) => text is null ? "" : text.Length <= 120 ? text : text[..117] + "...";
}
