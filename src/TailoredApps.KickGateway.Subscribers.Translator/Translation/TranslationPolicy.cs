using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subscribers.Translator.Translation;

/// <summary>Which slices are worth a provider call. Pure — the consumer only applies the verdict.</summary>
public static class TranslationPolicy
{
    /// <summary>Returns null when the slice should be translated, else the reason it is skipped.</summary>
    public static string? SkipReason(LiveTranscript t, TranslatorOptions opts, DateTime now)
    {
        var language = (t.Language ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(t.Text)) return "empty";
        if (language.Length == 0) return "no-language";
        if (language == opts.NormalizedTargetLanguage) return "already-target";
        if (!opts.NormalizedSourceLanguages.Contains(language)) return "language-not-configured";
        if (t.Confidence > 0 && t.Confidence < opts.MinConfidence) return "low-confidence";
        if (t.TranscribedAt != default && now - t.TranscribedAt > TimeSpan.FromSeconds(opts.MaxAgeSeconds)) return "stale";
        var channels = opts.NormalizedChannels;
        if (channels.Length > 0 && !channels.Contains((t.BroadcasterSlug ?? "").Trim().ToLowerInvariant())) return "channel-not-configured";
        return null;
    }
}
