using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Api.Transcripts;

/// <summary>
/// Persists every <see cref="LiveTranscript"/> published by <c>Subscribers.Transcriber</c> into
/// <see cref="KickGatewayDbContext.LiveTranscripts"/>. Runs on a shared durable queue (competing
/// consumers across Api replicas), so each transcript is stored once; broker redeliveries are
/// absorbed by <see cref="LiveTranscriptRecord.DedupeKey"/> (checked first, and enforced by a unique
/// index for the race between two replicas).
/// </summary>
public sealed class LiveTranscriptConsumer : IConsumer<LiveTranscript>
{
    private const int MaxTextLength = 4000;

    private readonly KickGatewayDbContext _db;
    private readonly ILogger<LiveTranscriptConsumer> _log;

    public LiveTranscriptConsumer(KickGatewayDbContext db, ILogger<LiveTranscriptConsumer> log)
    {
        _db = db;
        _log = log;
    }

    public async Task Consume(ConsumeContext<LiveTranscript> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        var slug = (m.BroadcasterSlug ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0 || string.IsNullOrWhiteSpace(m.Text))
        {
            _log.LogDebug("Ignoring LiveTranscript without slug/text");
            return;
        }

        var record = Map(m, slug);

        if (await _db.LiveTranscripts.AnyAsync(x => x.DedupeKey == record.DedupeKey, ct))
        {
            _log.LogDebug("[{Slug}] transcript {Key} already stored — redelivery ignored", slug, record.DedupeKey);
            return;
        }

        _db.LiveTranscripts.Add(record);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.Entry(record).State = EntityState.Detached;
            _log.LogDebug("[{Slug}] transcript {Key} stored concurrently by another replica — ignored", slug, record.DedupeKey);
        }
    }

    /// <summary>Contract → row. Public so tests and backfills can reuse the exact mapping.</summary>
    public static LiveTranscriptRecord Map(LiveTranscript m, string slug)
    {
        var text = m.Text.Trim();
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];
        var segments = m.Segments ?? [];

        return new LiveTranscriptRecord
        {
            DedupeKey = LiveTranscriptRecord.BuildDedupeKey(slug, m.StartedAt, m.AudioStartSeconds),
            ChannelSlug = slug,
            KickChannelId = m.KickChannelId ?? "",
            StartedAt = m.StartedAt,
            EndedAt = m.EndedAt,
            AudioStartSeconds = m.AudioStartSeconds,
            AudioSeconds = m.AudioSeconds,
            Text = text,
            Language = Code(m.Language) ?? "",
            DetectedLanguage = Code(m.DetectedLanguage),
            LanguageProbability = m.LanguageProbability is { } p ? Math.Clamp(p, 0f, 1f) : null,
            Confidence = m.Confidence,
            SegmentsJson = TranscriptJson.SerializeSegments(segments),
            SegmentCount = segments.Length,
            FirstMediaSequence = m.FirstMediaSequence,
            LastMediaSequence = m.LastMediaSequence,
            Model = m.Model ?? "",
            TranscribedAt = m.TranscribedAt,
            ProcessingSeconds = m.ProcessingSeconds,
            ReceivedAt = DateTime.UtcNow,
        };
    }

    /// <summary>Language codes are stored lowercase and clipped to the column (Whisper ids are 2–3 letters).</summary>
    private static string? Code(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var code = language.Trim().ToLowerInvariant();
        return code.Length > 16 ? code[..16] : code;
    }

    // SQL Server: 2627 = unique constraint, 2601 = unique index.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
