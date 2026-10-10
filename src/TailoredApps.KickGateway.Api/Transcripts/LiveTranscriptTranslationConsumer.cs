using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Api.Transcripts;

/// <summary>
/// Persists every <see cref="LiveTranscriptTranslation"/> published by <c>Subscribers.Translator</c> into
/// <see cref="KickGatewayDbContext.LiveTranscriptTranslations"/>, keyed on the transcript it belongs to
/// (<see cref="LiveTranscriptRecord.BuildDedupeKey"/>) + target language. The transcript row itself may
/// arrive a moment later or never (it is a separate best-effort stream), so this is a plain upsert without
/// a foreign key; the read side joins on the key. First translation for a key wins; redeliveries are
/// absorbed by the unique index.
/// </summary>
public sealed class LiveTranscriptTranslationConsumer : IConsumer<LiveTranscriptTranslation>
{
    private const int MaxTextLength = 4000;

    private readonly KickGatewayDbContext _db;
    private readonly ILogger<LiveTranscriptTranslationConsumer> _log;

    public LiveTranscriptTranslationConsumer(KickGatewayDbContext db, ILogger<LiveTranscriptTranslationConsumer> log)
    {
        _db = db;
        _log = log;
    }

    public async Task Consume(ConsumeContext<LiveTranscriptTranslation> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        var slug = (m.BroadcasterSlug ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0 || string.IsNullOrWhiteSpace(m.Text) || string.IsNullOrWhiteSpace(m.TargetLanguage))
        {
            _log.LogDebug("Ignoring LiveTranscriptTranslation without slug/text/language");
            return;
        }

        var record = Map(m, slug);

        if (await _db.LiveTranscriptTranslations.AnyAsync(x => x.TranscriptDedupeKey == record.TranscriptDedupeKey && x.TargetLanguage == record.TargetLanguage, ct))
        {
            _log.LogDebug("[{Slug}] translation {Key}/{Lang} already stored — ignored", slug, record.TranscriptDedupeKey, record.TargetLanguage);
            return;
        }

        _db.LiveTranscriptTranslations.Add(record);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            _db.Entry(record).State = EntityState.Detached;
            _log.LogDebug("[{Slug}] translation {Key}/{Lang} stored concurrently by another replica — ignored", slug, record.TranscriptDedupeKey, record.TargetLanguage);
        }
    }

    /// <summary>Contract → row. Public so tests can reuse the exact mapping.</summary>
    public static LiveTranscriptTranslationRecord Map(LiveTranscriptTranslation m, string slug)
    {
        var text = m.Text.Trim();
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];
        var segments = m.Segments ?? [];

        return new LiveTranscriptTranslationRecord
        {
            TranscriptDedupeKey = LiveTranscriptRecord.BuildDedupeKey(slug, m.StartedAt, m.AudioStartSeconds),
            ChannelSlug = slug,
            StartedAt = m.StartedAt,
            EndedAt = m.EndedAt,
            AudioStartSeconds = m.AudioStartSeconds,
            SourceLanguage = Code(m.SourceLanguage),
            TargetLanguage = Code(m.TargetLanguage),
            Text = text,
            SegmentsJson = TranscriptJson.SerializeSegments(segments),
            SegmentCount = segments.Length,
            Provider = (m.Provider ?? "").Trim() is { Length: > 120 } p ? p[..120] : (m.Provider ?? "").Trim(),
            TranslatedAt = m.TranslatedAt,
            ProcessingSeconds = m.ProcessingSeconds,
            ReceivedAt = DateTime.UtcNow,
        };
    }

    private static string Code(string? language)
    {
        var code = (language ?? "").Trim().ToLowerInvariant();
        return code.Length > 16 ? code[..16] : code;
    }
}
