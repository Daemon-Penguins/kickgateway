using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.LiveFeed;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.TwitchGateway.Contracts.Media;

namespace TailoredApps.KickGateway.Api.Transcripts;

/// <summary>
/// Persists every <see cref="TwitchLiveTranscript"/> (the Twitch transcriber's output) into the same
/// <see cref="KickGatewayDbContext.LiveTranscripts"/> table with <c>Platform = twitch</c> and a platform-prefixed
/// dedupe key, so the timeline / reactions / coverage queries serve both platforms. Shared durable queue,
/// redeliveries absorbed by the unique dedupe key - the Kick consumer's shape, landing-page push included.
/// </summary>
public sealed class TwitchLiveTranscriptConsumer : IConsumer<TwitchLiveTranscript>
{
    private readonly KickGatewayDbContext _db;
    private readonly HomeFeed _home;
    private readonly ILogger<TwitchLiveTranscriptConsumer> _log;

    public TwitchLiveTranscriptConsumer(KickGatewayDbContext db, HomeFeed home, ILogger<TwitchLiveTranscriptConsumer> log)
    {
        _db = db;
        _home = home;
        _log = log;
    }

    public async Task Consume(ConsumeContext<TwitchLiveTranscript> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;
        var login = (m.BroadcasterLogin ?? "").Trim().ToLowerInvariant();
        if (login.Length == 0 || string.IsNullOrWhiteSpace(m.Text))
        {
            _log.LogDebug("Ignoring TwitchLiveTranscript without login/text");
            return;
        }

        var record = LiveTranscriptConsumer.Map(ToKickShape(m), login);
        record.Platform = ChatPlatforms.Twitch;
        record.DedupeKey = LiveTranscriptRecord.BuildDedupeKey(ChatPlatforms.Twitch, login, m.StartedAt, m.AudioStartSeconds);

        if (await _db.LiveTranscripts.AnyAsync(x => x.DedupeKey == record.DedupeKey, ct))
        {
            _log.LogDebug("[twitch:{Login}] transcript {Key} already stored - redelivery ignored", login, record.DedupeKey);
            return;
        }

        _db.LiveTranscripts.Add(record);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            _db.Entry(record).State = EntityState.Detached;
            _log.LogDebug("[twitch:{Login}] transcript {Key} stored concurrently by another replica - ignored", login, record.DedupeKey);
            return;
        }

        _home.Append(LiveTranscriptConsumer.ToFeedEntry(record));
    }

    /// <summary>The Twitch contract in the Kick contract's shape, so one <c>Map</c> keeps the row logic in one place.</summary>
    public static LiveTranscript ToKickShape(TwitchLiveTranscript m) => new()
    {
        BroadcasterSlug = m.BroadcasterLogin,
        KickChannelId = m.BroadcasterUserId,
        StartedAt = m.StartedAt,
        EndedAt = m.EndedAt,
        AudioStartSeconds = m.AudioStartSeconds,
        AudioSeconds = m.AudioSeconds,
        Text = m.Text,
        Language = m.Language,
        DetectedLanguage = m.DetectedLanguage,
        LanguageProbability = m.LanguageProbability,
        Confidence = m.Confidence,
        Segments = (m.Segments ?? []).Select(s => new LiveTranscriptSegment { StartedAt = s.StartedAt, EndedAt = s.EndedAt, Text = s.Text, Confidence = s.Confidence }).ToArray(),
        FirstMediaSequence = m.FirstMediaSequence,
        LastMediaSequence = m.LastMediaSequence,
        Model = m.Model,
        TranscribedAt = m.TranscribedAt,
        ProcessingSeconds = m.ProcessingSeconds,
    };
}
