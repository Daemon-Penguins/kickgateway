using System.Diagnostics;
using System.Runtime.InteropServices;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.Transcriber.Sinks;
using Whisper.net;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;

/// <summary>
/// The single Whisper inference loop. Loads the model once, warms it up (the first GPU call compiles
/// shaders and can take a long time), then drains the coordinator's chunk queue one chunk at a time:
/// Whisper → <see cref="TranscriptFilter"/> → <see cref="LiveTranscript"/> → every registered sink.
/// One model serves all channels: processors run without cross-call context, so channels never bleed
/// into each other, and whisper.cpp is not safe to run concurrently on one context anyway.
/// <para>
/// Language: with <see cref="TranscriberOptions.Languages"/> set, every chunk first goes through Whisper's
/// language detector restricted to those candidates; <see cref="LanguageTracker"/> turns the reading into a
/// sticky per-channel language (a confident other language switches, an unsure one keeps the current). The
/// chunk is then transcribed by a processor built for that language (one per language, same loaded model,
/// language-specific prompt), and the transcript carries both the language used and what was detected.
/// </para>
/// <para>
/// Native failures (an <see cref="SEHException"/> is what a C++ exception from ggml/Vulkan looks like
/// from here — device lost, out of GPU memory, …) are recovered in escalating steps: rebuild the
/// processor, then reload the model, then drop to the CPU backend, and only after that give up and
/// stop the host so the orchestrator restarts the process.
/// </para>
/// </summary>
public sealed class WhisperWorker : BackgroundService
{
    private static readonly TimeSpan LagWarnInterval = TimeSpan.FromMinutes(1);

    /// <summary>whisper.cpp applies suppress_regex with regex_match over each vocabulary token, so the pattern must match the whole token.</summary>
    private const string UncensorRegex = @".*\*.*";
    private const string PolishProfanityPrompt = "Kurwa, chuj, pierdolić, jebać, zajebiście, spierdalaj, pojebane.";
    private const string EnglishProfanityPrompt = "Fuck, shit, bitch, asshole, motherfucker.";
    private const string GermanProfanityPrompt = "Scheiße, verdammt, Arschloch, fick dich, Hurensohn, verfickt.";

    /// <summary>
    /// Every word of the profanity prompts, for <see cref="TranscriptFilter"/>'s prompt-echo rule: on music or
    /// noise Whisper tends to "read back" the prompt ("pojebane, pojebane, pojebane", "shit, bitch, shit, bitch").
    /// </summary>
    public static IReadOnlyList<string> ProfanityVocabulary { get; } =
        (PolishProfanityPrompt + " " + EnglishProfanityPrompt + " " + GermanProfanityPrompt)
            .Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Below this a chunk is too short for a reliable language reading (an idle flush of a word or two) — the channel keeps its language.</summary>
    private const double MinDetectSeconds = 3.0;

    private const int FailuresBeforeModelReload = 2;
    private const int FailuresBeforeCpuFallback = 3;
    private const int FailuresBeforeGivingUp = 5;

    /// <summary>The very first GPU warm-up compiles shaders and can legitimately take a while.</summary>
    private static readonly TimeSpan FirstWarmUpTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(20);

    private readonly TranscriberOptions _opts;
    private readonly TranscriptionCoordinator _coordinator;
    private readonly WhisperModelProvider _models;
    private readonly TranscriptFilter _filter;
    private readonly ITranscriptSink[] _sinks;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WhisperWorker> _log;
    private readonly TimeSpan _inferenceTimeout;
    private bool _warmedUpOnce;

    private readonly LanguageTracker _languages;
    private readonly string[] _candidates;

    private string _modelPath = "";
    private WhisperFactory? _factory;
    /// <summary>Primary processor: the fallback language (or <c>auto</c>). Warmed up at start; also runs the language detector.</summary>
    private WhisperProcessor? _processor;
    /// <summary>Processors by language (the language token and the prompt differ), built lazily from the same loaded model.</summary>
    private readonly Dictionary<string, WhisperProcessor> _byLanguage = new(StringComparer.OrdinalIgnoreCase);
    private bool _cpuFallback;
    private int _consecutiveFailures;

    private DateTime _lastLagWarn = DateTime.MinValue;
    private long _processed, _emitted, _failed;

    public WhisperWorker(
        TranscriberOptions opts,
        TranscriptionCoordinator coordinator,
        WhisperModelProvider models,
        TranscriptFilter filter,
        IEnumerable<ITranscriptSink> sinks,
        IHostApplicationLifetime lifetime,
        ILogger<WhisperWorker> log)
    {
        _opts = opts;
        _coordinator = coordinator;
        _models = models;
        _filter = filter;
        _sinks = sinks.ToArray();
        _lifetime = lifetime;
        _log = log;
        _inferenceTimeout = TimeSpan.FromSeconds(opts.InferenceTimeoutSeconds);
        _candidates = opts.CandidateLanguages;
        _languages = new LanguageTracker(opts.FallbackLanguage, opts.LanguageSwitchMinProbability, opts.LanguageSwitchConfirmChunks);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // don't block host startup while the model loads

        _modelPath = await _models.EnsureModelAsync(stoppingToken);
        await EnsureEngineAsync(stoppingToken);

        _log.LogInformation("Transcriber online — model {Model}, language {Lang}, ~{Chunk}s chunks, sinks: {Sinks}",
            _models.ModelName, _opts.DescribeLanguageMode(), _opts.ChunkSeconds, string.Join(", ", _sinks.Select(s => s.Name)));

        try
        {
            await foreach (var job in _coordinator.Jobs.ReadAllAsync(stoppingToken))
                await ProcessWithRecoveryAsync(job, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            await DisposeEngineAsync(disposeFactory: true);
        }

        _log.LogInformation("Transcriber stopped — {Processed} chunk(s) processed, {Emitted} transcript(s) emitted, {Failed} failed, {Dropped} dropped",
            _processed, _emitted, _failed, _coordinator.DroppedChunks);
    }

    /// <summary>Runs one chunk under the watchdog; on a failure or hang rebuilds the engine and retries the chunk once.</summary>
    private async Task ProcessWithRecoveryAsync(TranscriptionJob job, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await EnsureEngineAsync(ct);
                await Watchdog.WithDeadlineAsync(token => ProcessAsync(job, token), _inferenceTimeout, $"transcribing a {job.Seconds:F1}s chunk", ct);
                _consecutiveFailures = 0;
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _failed++;
                _consecutiveFailures++;
                _log.LogError(ex, "[{Slug}] transcription of a {Sec:F1}s chunk failed (attempt {Attempt}, {Consecutive} consecutive failure(s))",
                    job.Slug, job.Seconds, attempt, _consecutiveFailures);
                await RecoverAsync(ct);
            }
        }

        _log.LogWarning("[{Slug}] giving up on a {Sec:F1}s chunk after the engine was rebuilt", job.Slug, job.Seconds);
    }

    /// <summary>Escalating recovery: processor → model reload → CPU backend → exit the process.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        if (_consecutiveFailures >= FailuresBeforeGivingUp)
            GiveUp("Whisper failed " + _consecutiveFailures + " times in a row even after reloading the model and falling back to the CPU");

        var reloadModel = _consecutiveFailures >= FailuresBeforeModelReload;
        if (!_cpuFallback && _opts.UseGpu && _consecutiveFailures >= FailuresBeforeCpuFallback)
        {
            _cpuFallback = true;
            reloadModel = true;
            _log.LogWarning("Whisper GPU backend keeps failing — falling back to the CPU backend (slower; restart the service to try the GPU again)");
        }

        _log.LogWarning("Rebuilding the Whisper {What}", reloadModel ? "model + processor" : "processor");
        await DisposeEngineAsync(disposeFactory: reloadModel);

        try
        {
            await EnsureEngineAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Couldn't even bring a fresh engine up (model load / warm-up failed or hung). Count it and
            // let the next attempt escalate further; the retry loop calls us again.
            _consecutiveFailures++;
            _log.LogError(ex, "Rebuilding the Whisper engine failed ({Consecutive} consecutive failure(s))", _consecutiveFailures);
            await DisposeEngineAsync(disposeFactory: true);
            if (_consecutiveFailures >= FailuresBeforeGivingUp)
                GiveUp("Whisper engine could not be rebuilt");
        }
    }

    /// <summary>
    /// Last resort. A hung native call can't be aborted and may also block a graceful shutdown, so ask the
    /// host to stop and hard-exit if it hasn't after a grace period — the orchestrator restarts the process.
    /// </summary>
    private void GiveUp(string reason)
    {
        _log.LogCritical("{Reason} — stopping the process so it gets restarted", reason);
        _lifetime.StopApplication();
        _ = Task.Delay(ExitGracePeriod).ContinueWith(_ =>
        {
            _log.LogCritical("Graceful shutdown did not complete in {Sec}s — exiting", ExitGracePeriod.TotalSeconds);
            Environment.Exit(3);
        }, TaskScheduler.Default);
        throw new InvalidOperationException("Whisper engine is unrecoverable: " + reason);
    }

    private async Task EnsureEngineAsync(CancellationToken ct)
    {
        if (_processor is not null) return;
        _factory ??= _models.CreateFactory(_modelPath, useGpu: _opts.UseGpu && !_cpuFallback);
        var primaryLanguage = _opts.DetectsLanguage ? _opts.FallbackLanguage : _opts.Language.Trim();
        var processor = BuildProcessor(_factory, primaryLanguage);
        try
        {
            var timeout = _warmedUpOnce ? _inferenceTimeout : FirstWarmUpTimeout;
            await Watchdog.WithDeadlineAsync(token => WarmUpAsync(processor, token), timeout, "Whisper warm-up", ct);
            _warmedUpOnce = true;
        }
        catch
        {
            await DisposeWithTimeoutAsync(() => processor.DisposeAsync().AsTask(), "processor");
            throw;
        }
        _processor = processor;
        _byLanguage[primaryLanguage] = processor;
    }

    /// <summary>The processor for <paramref name="language"/>; built on first use from the already loaded model (no warm-up needed — shaders are compiled per model, not per processor).</summary>
    private WhisperProcessor ProcessorFor(string language)
    {
        if (_byLanguage.TryGetValue(language, out var existing)) return existing;
        var processor = BuildProcessor(_factory!, language);
        _byLanguage[language] = processor;
        _log.LogInformation("Whisper processor for language {Lang} ready", language);
        return processor;
    }

    private async Task DisposeEngineAsync(bool disposeFactory)
    {
        var processors = _byLanguage.Values.Distinct().ToList();
        if (_processor is not null && !processors.Contains(_processor)) processors.Add(_processor);
        _processor = null;
        _byLanguage.Clear();
        foreach (var p in processors)
            await DisposeWithTimeoutAsync(() => p.DisposeAsync().AsTask(), "processor");
        if (disposeFactory && _factory is not null)
        {
            var f = _factory;
            _factory = null;
            await DisposeWithTimeoutAsync(() => Task.Run(f.Dispose), "model");
        }
    }

    /// <summary>Disposal of a hung native object may itself block; wait a little, then abandon it (a leak beats a hang).</summary>
    private async Task DisposeWithTimeoutAsync(Func<Task> dispose, string what)
    {
        try
        {
            await dispose().WaitAsync(DisposeTimeout);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("Disposing the Whisper {What} did not finish in {Sec}s — abandoning it", what, DisposeTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Disposing the Whisper {What} failed", what);
        }
    }

    private WhisperProcessor BuildProcessor(WhisperFactory factory, string language)
    {
        var b = factory.CreateBuilder();

        if (string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)) b.WithLanguageDetection(); // language "" → whisper.cpp detects, then transcribes
        else b.WithLanguage(language);

        b.WithThreads(_opts.EffectiveThreads);
        var prompt = BuildPrompt(language);
        if (prompt is not null) b.WithPrompt(prompt);
        if (_opts.BeamSize > 0) b.WithBeamSearchSamplingStrategy(s => s.WithBeamSize(_opts.BeamSize));

        b.WithNoSpeechThreshold(_opts.MaxNoSpeechProbability);
        if (_opts.SuppressCensorTokens) b.WithSuppressRegex(UncensorRegex); // costly: whisper.cpp regex-matches the whole vocabulary per generated token
        b.WithProbabilities(); // needed for SegmentData.Probability (confidence filter)

        return b.Build();
    }

    /// <summary>
    /// Initial prompt = the configured names/slang plus, when <see cref="TranscriberOptions.Uncensored"/>,
    /// a few swear words spelled out in the stream language so the model stops writing k***a.
    /// </summary>
    private string? BuildPrompt(string language)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(_opts.Prompt)) parts.Add(_opts.Prompt.Trim());
        if (_opts.Uncensored) parts.Add(ProfanityPrompt(language));
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string ProfanityPrompt(string language) => language.ToLowerInvariant() switch
    {
        "pl" => PolishProfanityPrompt,
        "en" => EnglishProfanityPrompt,
        "de" => GermanProfanityPrompt,
        _ => PolishProfanityPrompt + " " + EnglishProfanityPrompt, // auto / other: cover both common stream languages
    };

    private async Task WarmUpAsync(WhisperProcessor processor, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await foreach (var _ in processor.ProcessAsync(new float[AudioDecoder.SampleRate], ct)) { }
        _log.LogInformation("Whisper warm-up took {Ms} ms", sw.ElapsedMilliseconds);
    }

    private async Task ProcessAsync(TranscriptionJob job, CancellationToken ct)
    {
        var waited = (DateTime.UtcNow - job.EnqueuedAt).TotalSeconds;
        var sw = Stopwatch.StartNew();

        var decision = DecideLanguage(job);
        var processor = _opts.DetectsLanguage ? ProcessorFor(decision.Language) : _processor!;

        var kept = new List<LiveTranscriptSegment>();
        string? language = null;

        await foreach (var seg in processor.ProcessAsync(job.Samples, ct))
        {
            language ??= seg.Language;
            var text = seg.Text?.Trim() ?? "";
            if (!_filter.Accept(text, seg.Probability, out var reason))
            {
                _log.LogDebug("[{Slug}] dropped segment ({Reason}, p={P:F2}): {Text}", job.Slug, reason, seg.Probability, text);
                continue;
            }

            kept.Add(new LiveTranscriptSegment
            {
                StartedAt = job.StartedAt + seg.Start,
                EndedAt = job.StartedAt + seg.End,
                Text = text,
                Confidence = seg.Probability,
            });
        }

        sw.Stop();
        _processed++;
        WarnIfLagging(job, waited, sw.Elapsed.TotalSeconds);

        // Loops: the same sentence emitted as several consecutive segments (each passes the per-segment filter).
        kept = TranscriptFilter.DropLoops(kept, s => s.Text,
            onDropped: (run, text) => _log.LogDebug("[{Slug}] dropped a loop of {Run} identical segments: {Text}", job.Slug, run, text));

        if (kept.Count == 0)
        {
            _log.LogDebug("[{Slug}] {Sec:F1}s chunk → no speech ({Ms} ms)", job.Slug, job.Seconds, sw.ElapsedMilliseconds);
            return;
        }

        var transcript = new LiveTranscript
        {
            BroadcasterSlug = job.Slug,
            KickChannelId = job.ChannelId,
            StartedAt = job.StartedAt,
            EndedAt = job.EndedAt,
            AudioStartSeconds = job.AudioStartSeconds,
            AudioSeconds = job.Seconds,
            Text = string.Join(' ', kept.Select(k => k.Text)),
            Language = ResolveLanguage(decision, language),
            DetectedLanguage = _opts.DetectsLanguage ? decision.Detected : (_opts.IsAutoLanguage ? NullIfAuto(language) : null),
            LanguageProbability = decision.Probability,
            Confidence = kept.Average(k => k.Confidence),
            Segments = kept.ToArray(),
            FirstMediaSequence = job.FirstMediaSequence,
            LastMediaSequence = job.LastMediaSequence,
            Model = _models.ModelName,
            TranscribedAt = DateTime.UtcNow,
            ProcessingSeconds = sw.Elapsed.TotalSeconds,
        };

        _emitted++;
        _log.LogInformation("[{Slug}] {Sec:F1}s [{Lang}] → {Chars} chars in {Ms} ms (queued {Wait:F1}s): {Preview}",
            job.Slug, job.Seconds, transcript.Language.Length == 0 ? "?" : transcript.Language, transcript.Text.Length, sw.ElapsedMilliseconds, waited, Preview(transcript.Text));

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.WriteAsync(transcript, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[{Slug}] transcript sink '{Sink}' failed", job.Slug, sink.Name);
            }
        }
    }

    /// <summary>
    /// Runs Whisper's language detector on the chunk, restricted to the configured candidates, and lets the
    /// tracker decide which language the chunk is transcribed in. Only when detection is on; fixed and
    /// <c>auto</c> modes resolve the language from the transcription itself.
    /// </summary>
    private LanguageDecision DecideLanguage(TranscriptionJob job)
    {
        if (!_opts.DetectsLanguage) return new LanguageDecision(_opts.Language.Trim(), null, null, false);
        if (job.Seconds < MinDetectSeconds) return _languages.Keep(job.Slug);

        var sw = Stopwatch.StartNew();
        var (detected, probability) = _processor!.DetectLanguageWithProbability(job.Samples, _candidates);
        var decision = _languages.Decide(job.Slug, detected, probability);

        if (decision.Switched)
            _log.LogInformation("[{Slug}] language → {Lang} (detector p={P:F2}, {Ms} ms)", job.Slug, decision.Language, probability, sw.ElapsedMilliseconds);
        else if (decision.PendingConfirmations > 0)
            _log.LogInformation("[{Slug}] detector hears {Detected} (p={P:F2}, {N}/{Needed}) — staying with {Lang} until confirmed", job.Slug, detected, probability, decision.PendingConfirmations, _opts.LanguageSwitchConfirmChunks, decision.Language);
        else if (detected is null)
            _log.LogDebug("[{Slug}] language detector gave no reading — staying with {Lang}", job.Slug, decision.Language);
        else
            _log.LogDebug("[{Slug}] language {Lang} (detector heard {Detected} p={P:F2}, {Ms} ms)", job.Slug, decision.Language, detected, probability, sw.ElapsedMilliseconds);
        return decision;
    }

    /// <summary>Language the slice was transcribed in: the tracker's choice, Whisper's pick in <c>auto</c> mode, else the fixed one.</summary>
    private string ResolveLanguage(LanguageDecision decision, string? segmentLanguage)
    {
        if (_opts.DetectsLanguage) return decision.Language;
        if (_opts.IsAutoLanguage) return NullIfAuto(segmentLanguage) ?? "";
        return _opts.Language.Trim();
    }

    private static string? NullIfAuto(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? null : language;

    private void WarnIfLagging(TranscriptionJob job, double waited, double processing)
    {
        // Slower than real time → the queue will fill and the coordinator will start dropping chunks.
        if (processing <= job.Seconds) return;
        var now = DateTime.UtcNow;
        if (now - _lastLagWarn < LagWarnInterval) return;
        _lastLagWarn = now;
        _log.LogWarning("[{Slug}] Whisper needed {Proc:F1}s for {Audio:F1}s of audio (queued {Wait:F1}s) — slower than real time. " +
                        "Consider a smaller model/quantization, the GPU, or fewer channels.",
            job.Slug, processing, job.Seconds, waited);
    }

    private static string Preview(string text) => text.Length <= 140 ? text : text[..137] + "...";
}
