namespace TailoredApps.KickGateway.Subscribers.Transcriber;

/// <summary>The <c>Transcriber</c> configuration section.</summary>
public sealed class TranscriberOptions
{
    public const string Section = "Transcriber";

    /// <summary>Channel slugs to transcribe (bound broker-side). Empty = every channel on the video exchange.</summary>
    public string[] Channels { get; set; } = [];

    /// <summary>
    /// Receive queue name. Change it when running several transcriber instances with different
    /// channel lists — instances sharing a queue would compete for segments and break per-channel order.
    /// </summary>
    public string QueueName { get; set; } = "transcriber";

    /// <summary>
    /// Starting / fallback language (ISO-639-1, e.g. <c>pl</c>): every channel is transcribed in it until the
    /// detector is confident about another candidate from <see cref="Languages"/>, and it is the fixed language
    /// when that list is empty. <c>auto</c> = Whisper's own unrestricted per-chunk detection (no candidate list,
    /// no stickiness; the language is whatever Whisper picked for the chunk).
    /// </summary>
    public string Language { get; set; } = "pl";

    /// <summary>
    /// Comma-separated candidate languages for per-chunk detection, e.g. <c>pl,en,de</c>. Each chunk first goes
    /// through Whisper's language detector restricted to these codes (one extra encoder pass, ~0.5 s on a GPU);
    /// a confident reading (<see cref="LanguageSwitchMinProbability"/>) switches the channel to that language,
    /// an unsure one keeps the channel's current language (see <c>LanguageTracker</c>). The transcript carries
    /// both the language used and what was detected. Empty = no detection, fixed <see cref="Language"/>.
    /// </summary>
    public string Languages { get; set; } = "pl,en,de";

    /// <summary>
    /// Detector probability (0..1) a reading needs to count towards a switch. Measured on live streams:
    /// background music and Whisper's silence fillers ("I'm sorry.") get 0.6–0.9 for the wrong language,
    /// genuine speech 0.8–0.99 — hence 0.7 together with <see cref="LanguageSwitchConfirmChunks"/>.
    /// </summary>
    public float LanguageSwitchMinProbability { get; set; } = 0.7f;

    /// <summary>
    /// How many consecutive chunks must confidently report the same other language before the channel
    /// switches to it. 1 = switch on the first confident chunk (reactive but flappy on music/clips);
    /// 2 (default) ignores one-off chunks and costs a genuine switch one chunk (~15 s) in the old language.
    /// </summary>
    public int LanguageSwitchConfirmChunks { get; set; } = 2;

    /// <summary><see cref="Language"/> is <c>auto</c> (Whisper's unrestricted detection).</summary>
    public bool IsAutoLanguage => string.Equals(Language?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Candidate codes parsed from <see cref="Languages"/> (lowercase, distinct, in the given order), with the
    /// fallback language added when it is a code and missing from the list. Empty = detection off.
    /// </summary>
    public string[] CandidateLanguages
    {
        get
        {
            var list = (Languages ?? "")
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.ToLowerInvariant())
                .Distinct()
                .ToList();
            if (list.Count > 0 && !IsAutoLanguage)
            {
                var fallback = (Language ?? "").Trim().ToLowerInvariant();
                if (fallback.Length > 0 && !list.Contains(fallback)) list.Insert(0, fallback);
            }
            return list.ToArray();
        }
    }

    /// <summary>Candidate-restricted detection with a sticky per-channel language is on.</summary>
    public bool DetectsLanguage => CandidateLanguages.Length > 0;

    /// <summary>Language a channel starts in and falls back to while detection is unsure (first candidate when <see cref="Language"/> is <c>auto</c>).</summary>
    public string FallbackLanguage => IsAutoLanguage
        ? (CandidateLanguages.FirstOrDefault() ?? "auto")
        : (Language ?? "").Trim().ToLowerInvariant();

    /// <summary>One-line description of the language setup for the startup log.</summary>
    public string DescribeLanguageMode() => DetectsLanguage
        ? $"{FallbackLanguage}, detecting among {string.Join(",", CandidateLanguages)} (switch after {LanguageSwitchConfirmChunks} chunk(s) at p>={LanguageSwitchMinProbability.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)})"
        : IsAutoLanguage ? "auto (Whisper picks per chunk)" : $"{Language} (fixed)";

    /// <summary><c>Whisper.net.Ggml.GgmlType</c> name, e.g. <c>LargeV3Turbo</c>, <c>Medium</c>, <c>Small</c>, <c>Base</c>.</summary>
    public string Model { get; set; } = "LargeV3Turbo";

    /// <summary><c>Whisper.net.Ggml.QuantizationType</c> name, e.g. <c>Q5_0</c>, <c>Q8_0</c> or <c>NoQuantization</c>.</summary>
    public string Quantization { get; set; } = "Q5_0";

    /// <summary>Where GGML models are cached (downloaded on first run). Default: LocalAppData/kickgateway/whisper.</summary>
    public string? ModelDir { get; set; }

    /// <summary>Use the GPU backend (Vulkan/CUDA) when the native runtime finds one; falls back to CPU otherwise.</summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>
    /// Index of the GPU to use when several are present (ggml enumerates them at startup — the list is
    /// logged as <c>ggml_vulkan: N = …</c>). On a box with an integrated + a discrete Intel GPU make sure
    /// this points at the discrete one.
    /// </summary>
    public int GpuDevice { get; set; }

    /// <summary>
    /// Disable ggml's Vulkan cooperative-matrix (KHR_coopmat) shaders. On Intel Arc (Pro B50, driver
    /// 32.0.101.88xx) they fault after ~10–80 inferences and leave the context hung; without them a
    /// chunk is ~45 % slower but the backend is stable. Applied by setting <c>GGML_VK_DISABLE_COOPMAT</c>
    /// in the process environment before the native library loads.
    /// </summary>
    public bool DisableVulkanCoopmat { get; set; } = true;

    /// <summary>
    /// Whisper CPU threads (used for the decoder on GPU, for everything on CPU). 0 = auto:
    /// <c>ProcessorCount − 2</c> clamped to 4..16 — whisper.cpp's own default is only 4.
    /// </summary>
    public int Threads { get; set; }

    /// <summary>Effective thread count after resolving the auto default.</summary>
    public int EffectiveThreads => Threads > 0 ? Threads : Math.Clamp(Environment.ProcessorCount - 2, 4, 16);

    /// <summary>Beam-search width. 0 = greedy decoding (fastest); 5 is Whisper's quality default but several times slower.</summary>
    public int BeamSize { get; set; }

    /// <summary>
    /// Optional initial prompt — names and slang the model should spell right (e.g. "Łazan, Petrusek, ciotka").
    /// Spelling a few swear words out here also nudges the model away from self-censoring.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// Whisper learned subtitle-style censoring (<c>k***a</c>, <c>********</c>) from its training data.
    /// When on, a short list of swear words in the stream's language is appended to the initial prompt;
    /// the model copies the prompt's style and writes profanity out verbatim. Cheap (a few prompt tokens).
    /// </summary>
    public bool Uncensored { get; set; } = true;

    /// <summary>
    /// Hard variant: ban every vocabulary token containing <c>*</c> during decoding (whisper.cpp
    /// <c>suppress_regex</c>). Effective, but whisper.cpp re-matches the regex against the whole
    /// ~51k-token vocabulary for <b>every generated token</b> — measured 3–14× slower inference
    /// (a 13 s chunk went from 0.8 s to 11 s on the GPU). Off by default; prefer <see cref="Uncensored"/>.
    /// </summary>
    public bool SuppressCensorTokens { get; set; }

    /// <summary>ffmpeg executable (on PATH or absolute) used to pull the audio track out of the segments.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Target chunk length in seconds; the cut lands on the quietest 100 ms shortly before this point. Whisper's window is 30 s, so stay below that.</summary>
    public double ChunkSeconds { get; set; } = 15;

    /// <summary>Chunks shorter than this are only flushed when the stream goes idle.</summary>
    public double MinChunkSeconds { get; set; } = 2;

    /// <summary>Chunks whose RMS is below this are treated as silence and never sent to Whisper (avoids hallucinations).</summary>
    public float SilenceRms { get; set; } = 0.006f;

    /// <summary>Whisper's no-speech threshold: segments with a higher no-speech probability (and a low log-prob) are dropped.</summary>
    public float MaxNoSpeechProbability { get; set; } = 0.6f;

    /// <summary>Segments whose average token probability is below this are dropped (0 = keep everything).</summary>
    public float MinConfidence { get; set; } = 0.35f;

    /// <summary>
    /// Phrases Whisper is known to hallucinate on silence/music (subtitle credits, "thanks for watching", …).
    /// A segment that is essentially one of these is dropped. Matching is case- and punctuation-insensitive.
    /// </summary>
    public string[] SuppressPhrases { get; set; } =
    [
        "Dziękuję za oglądanie",
        "Dziękuję za uwagę",
        "Dzięki za oglądanie",
        "Napisy stworzone przez społeczność Amara.org",
        "Napisy wykonane przez społeczność Amara.org",
        "Do zobaczenia w następnym odcinku",
        "Zapraszam do subskrypcji",
        "Wszystkie prawa zastrzeżone",
        "Thanks for watching",
        "Thank you for watching",
        "Subtitles by the Amara.org community",
        "Subscribe to my channel",
        "Transcription by CastingWords",
        "All rights reserved",
    ];

    /// <summary>Flush the buffered audio of a channel after this many seconds without new samples (stream ended / stalled).</summary>
    public double IdleFlushSeconds { get; set; } = 8;

    /// <summary>Tear a channel's decoder down after this many seconds without a new segment; it is recreated on the next one.</summary>
    public double SessionTimeoutSeconds { get; set; } = 120;

    /// <summary>How many chunks may wait for Whisper; the oldest are dropped when it can't keep up (stay live rather than fall behind).</summary>
    public int MaxPendingChunks { get; set; } = 8;

    /// <summary>
    /// Watchdog: a single Whisper call that takes longer than this is treated as hung (a GPU fault can
    /// leave the native call blocked forever). The processor is abandoned and rebuilt; repeated hangs
    /// escalate to a model reload, the CPU backend, and finally a process exit so the orchestrator restarts it.
    /// </summary>
    public double InferenceTimeoutSeconds { get; set; } = 60;

    /// <summary>Publish each transcript as <c>Contracts.Realtime.Media.LiveTranscript</c> on the bus.</summary>
    public bool PublishToBus { get; set; } = true;

    /// <summary>Append each transcript to per-channel, per-day <c>.txt</c> + <c>.jsonl</c> files under <see cref="OutputDir"/>.</summary>
    public bool WriteFiles { get; set; } = true;

    /// <summary>Where transcript files go. Default: temp/kickgateway-transcripts.</summary>
    public string? OutputDir { get; set; }

    /// <summary>Throws with a clear message when the section is inconsistent; called once at startup.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(QueueName)) throw new ArgumentException("Transcriber:QueueName must not be empty.");
        if (string.IsNullOrWhiteSpace(Language)) throw new ArgumentException("Transcriber:Language must be a language code or 'auto'.");
        if (!IsAutoLanguage && !IsLanguageCode(Language.Trim().ToLowerInvariant())) throw new ArgumentException($"Transcriber:Language '{Language}' is not an ISO-639-1 code (pl, en, de, ...) or 'auto'.");
        foreach (var code in CandidateLanguages)
            if (!IsLanguageCode(code)) throw new ArgumentException($"Transcriber:Languages contains '{code}' — use comma-separated ISO-639-1 codes like pl,en,de.");
        if (LanguageSwitchMinProbability is <= 0 or > 1) throw new ArgumentException("Transcriber:LanguageSwitchMinProbability must be within (0, 1].");
        if (LanguageSwitchConfirmChunks is < 1 or > 10) throw new ArgumentException("Transcriber:LanguageSwitchConfirmChunks must be between 1 and 10.");
        if (ChunkSeconds is < 3 or > 30) throw new ArgumentException("Transcriber:ChunkSeconds must be between 3 and 30 (Whisper's window is 30 s).");
        if (MinChunkSeconds < 0 || MinChunkSeconds >= ChunkSeconds) throw new ArgumentException("Transcriber:MinChunkSeconds must be >= 0 and below ChunkSeconds.");
        if (IdleFlushSeconds <= 0) throw new ArgumentException("Transcriber:IdleFlushSeconds must be positive.");
        if (SessionTimeoutSeconds < IdleFlushSeconds) throw new ArgumentException("Transcriber:SessionTimeoutSeconds must be at least IdleFlushSeconds.");
        if (MaxPendingChunks < 1) throw new ArgumentException("Transcriber:MaxPendingChunks must be at least 1.");
        if (InferenceTimeoutSeconds < 5) throw new ArgumentException("Transcriber:InferenceTimeoutSeconds must be at least 5.");
        if (BeamSize < 0) throw new ArgumentException("Transcriber:BeamSize must be 0 (greedy) or positive.");
        if (string.IsNullOrWhiteSpace(FfmpegPath)) throw new ArgumentException("Transcriber:FfmpegPath must not be empty.");
    }

    /// <summary>Whisper's language ids are 2–3 lowercase ASCII letters (<c>pl</c>, <c>en</c>, <c>yue</c>, …).</summary>
    private static bool IsLanguageCode(string code) => code.Length is 2 or 3 && code.All(char.IsAsciiLetterLower);
}
