using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Transcripts;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// <see cref="SpeechReactionQueries"/> (what chat wrote after one speech slice) and <see cref="TranscriptCoverageQueries"/>
/// (how much speech-to-text exists, for /status and /overview) against a real SQL Server. Skipped without Docker.
/// </summary>
public class SpeechReactionsIntegrationTests(ChatAnalyticsDatabase fixture) : IClassFixture<ChatAnalyticsDatabase>
{
    private static readonly DateTime T0 = new(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task Reactions_follow_the_slice_with_a_rate_against_the_minute_before()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var cs = await fixture.CreateDatabaseAsync("reactions");
        await using var db = ChatAnalyticsDatabase.Open(cs);
        await SeedAsync(db);
        var joke = await db.LiveTranscripts.AsNoTracking().SingleAsync(x => x.Text == "the joke");

        var r = await SpeechReactionQueries.ForSliceAsync(db, "alpha", joke.Id, 5, 45, 100, default);
        Assert.NotNull(r);
        Assert.Equal("alpha", r.Channel);
        Assert.Equal("the joke", r.Slice.Text);
        Assert.Empty(r.Slice.Segments);
        Assert.Equal("co za zart", r.Slice.Translations!["pl"]);
        Assert.Equal("earlier", r.Previous!.Text);
        Assert.Equal("after", r.Next!.Text);
        Assert.Equal(T0.AddSeconds(25), r.Window.From);
        Assert.Equal(T0.AddSeconds(65), r.Window.To);
        Assert.Equal(5, r.LagMinSeconds);
        Assert.Equal(45, r.LagMaxSeconds);

        // the window is [endedAt+5, endedAt+45): m0 at +24 is before it, m6 at +65 is on the closed end, beta never leaks in
        Assert.Equal(new[] { "m1", "m2", "m3", "m4" }, r.Messages.Select(m => m.MessageId));
        Assert.Equal(6, r.Messages[0].SecondsAfterEnd);
        Assert.Equal("Alice", r.Messages[0].Username);
        Assert.Equal("m1", r.Messages[2].ReplyToMessageId);
        Assert.Equal("Alice", r.Messages[2].ReplyToUsername);
        Assert.False(r.Truncated);

        Assert.Equal(4, r.Stats.Messages);
        Assert.Equal(3, r.Stats.Chatters);
        Assert.Equal(6, r.Stats.PerMinute);           // 4 messages in 40 s
        Assert.Equal(2, r.Stats.BaselineMessages);    // b1 at -50 and b2 at -10, in the minute before startedAt
        Assert.Equal(2, r.Stats.BaselinePerMinute);
        Assert.Equal(3, r.Stats.Ratio);
        Assert.Equal("m1", r.Stats.FirstReply!.MessageId);
        Assert.Contains("endedAt + lagMin", r.Chronology);

        // limit caps the list, never the stats
        var two = await SpeechReactionQueries.ForSliceAsync(db, "alpha", joke.Id, 5, 45, 2, default);
        Assert.Equal(new[] { "m1", "m2" }, two!.Messages.Select(m => m.MessageId));
        Assert.True(two.Truncated);
        Assert.Equal(4, two.Stats.Messages);
        Assert.Equal(3, two.Stats.Chatters);

        // a custom lag moves the window
        var tight = await SpeechReactionQueries.ForSliceAsync(db, "alpha", joke.Id, 0, 10, 100, default);
        Assert.Equal(new[] { "m0", "m1" }, tight!.Messages.Select(m => m.MessageId));
        Assert.Equal(12, tight.Stats.PerMinute);      // 2 messages in 10 s
        Assert.Equal(6, tight.Stats.Ratio);

        // the first slice has no predecessor; a slice nobody reacted to has no first reply
        var earlier = await db.LiveTranscripts.AsNoTracking().SingleAsync(x => x.Text == "earlier");
        var first = await SpeechReactionQueries.ForSliceAsync(db, "alpha", earlier.Id, 5, 45, 100, default);
        Assert.Null(first!.Previous);
        Assert.Equal("the joke", first.Next!.Text);
        Assert.Equal(new[] { "b2", "m0" }, first.Messages.Select(m => m.MessageId)); // [T0-15, T0+25)
        Assert.Equal(1, first.Stats.BaselineMessages);                                 // b1 in [T0-100, T0-40)

        var betaOld = await db.LiveTranscripts.AsNoTracking().SingleAsync(x => x.Text == "beta old");
        var silent = await SpeechReactionQueries.ForSliceAsync(db, "beta", betaOld.Id, 5, 45, 100, default);
        Assert.Empty(silent!.Messages);
        Assert.Equal(0, silent.Stats.Messages);
        Assert.Equal(0, silent.Stats.PerMinute);
        Assert.Null(silent.Stats.Ratio);
        Assert.Null(silent.Stats.FirstReply);
        Assert.Null(silent.Previous);
        Assert.Null(silent.Next);

        // unknown id, or an id that belongs to another channel
        Assert.Null(await SpeechReactionQueries.ForSliceAsync(db, "alpha", 999_999, 5, 45, 100, default));
        Assert.Null(await SpeechReactionQueries.ForSliceAsync(db, "beta", joke.Id, 5, 45, 100, default));
    }

    [SkippableFact]
    public async Task Coverage_reports_extent_freshness_and_recent_languages_per_channel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var cs = await fixture.CreateDatabaseAsync("coverage");
        await using var db = ChatAnalyticsDatabase.Open(cs);
        await SeedAsync(db);
        var now = T0.AddHours(1);

        var status = await TranscriptCoverageQueries.StatusAsync(db, AnalyticsScope.Unrestricted, now, default);
        Assert.Equal(4, status.Slices);
        Assert.Equal(new[] { "alpha", "beta" }, status.Channels.Select(c => c.Channel));

        var alpha = status.Channels[0];
        Assert.Equal(3, alpha.Slices);
        Assert.Equal(T0.AddSeconds(-40), alpha.FirstStartedAt);
        Assert.Equal(T0.AddSeconds(30), alpha.LastStartedAt);
        Assert.Equal(T0.AddSeconds(40), alpha.LastEndedAt);
        Assert.Equal(T0.AddSeconds(52), alpha.LastReceivedAt);
        Assert.Equal(12, alpha.LastReceiveLagSeconds);
        Assert.Equal(now - TranscriptCoverageQueries.RecentSpan, alpha.Last24h.Window.From);
        Assert.Equal(now, alpha.Last24h.Window.To);
        Assert.Equal(3, alpha.Last24h.Slices);
        Assert.Equal(50, alpha.Last24h.SpeechSeconds);
        Assert.Equal(new[] { ("pl", 2), ("de", 1) }, alpha.Last24h.Languages.Select(kv => (kv.Key, kv.Value)));

        // beta's only slice is 30 h old: it counts all-time, not in the last 24 h
        var beta = status.Channels[1];
        Assert.Equal(1, beta.Slices);
        Assert.Equal(T0.AddHours(-30), beta.LastStartedAt);
        Assert.Equal(0, beta.Last24h.Slices);
        Assert.Empty(beta.Last24h.Languages);
        Assert.Null(beta.Last24h.AverageConfidence);

        // the scope restricts the channels, like every other analytics query
        var betaOnly = await TranscriptCoverageQueries.StatusAsync(db, AnalyticsScope.ForSlugs(["beta"]), now, default);
        Assert.Equal(1, betaOnly.Slices);
        Assert.Equal("beta", Assert.Single(betaOnly.Channels).Channel);
        var nothing = await TranscriptCoverageQueries.StatusAsync(db, AnalyticsScope.ForSlugs(["gamma"]), now, default);
        Assert.Equal(0, nothing.Slices);
        Assert.Empty(nothing.Channels);

        // the overview-style window counts slices that START in it
        var w = await TranscriptCoverageQueries.WindowAsync(db, "alpha", new AnalyticsWindow(T0, T0.AddSeconds(100)), default);
        Assert.Equal(2, w.Slices);
        Assert.Equal(30, w.SpeechSeconds);
        Assert.Equal(T0, w.FirstStartedAt);
        Assert.Equal(T0.AddSeconds(30), w.LastStartedAt);
        Assert.Equal(0.8, w.AverageConfidence);
        Assert.Equal(new[] { ("de", 1), ("pl", 1) }, w.Languages.Select(kv => (kv.Key, kv.Value)));

        var none = await TranscriptCoverageQueries.WindowAsync(db, "alpha", new AnalyticsWindow(T0.AddHours(2), T0.AddHours(3)), default);
        Assert.Equal(0, none.Slices);
        Assert.Equal(0, none.SpeechSeconds);
        Assert.Null(none.AverageConfidence);
        Assert.Null(none.FirstStartedAt);
        Assert.Empty(none.Languages);

        var unbounded = await TranscriptCoverageQueries.WindowAsync(db, "beta", new AnalyticsWindow(null, now), default);
        Assert.Equal(1, unbounded.Slices);
    }

    private static async Task SeedAsync(KickGatewayDbContext db)
    {
        db.LiveTranscripts.AddRange(
            Speech("alpha", -40, 20, "earlier"),
            Speech("alpha", 0, 20, "the joke", language: "de"),
            Speech("alpha", 30, 10, "after", confidence: 0.7f),
            Speech("beta", -30 * 3600, 10, "beta old"));

        db.ChatMessages.AddRange(
            Chat("alpha", "b1", -50, "4", "Dave", "before"),
            Chat("alpha", "b2", -10, "4", "Dave", "still before"),
            Chat("alpha", "m0", 24, "3", "Carol", "early"),
            Chat("alpha", "m1", 26, "1", "Alice", "lol"),
            Chat("alpha", "m2", 30, "2", "Bob", "haha"),
            Chat("alpha", "m3", 30, "1", "Alice", "LOL", replyTo: ("m1", "1", "Alice")),
            Chat("alpha", "m4", 64, "3", "Carol", "late"),
            Chat("alpha", "m6", 65, "2", "Bob", "boundary"),
            Chat("beta", "bm1", 30, "6", "Frank", "beta chat"));

        var joke = db.LiveTranscripts.Local.Single(t => t.Text == "the joke");
        db.LiveTranscriptTranslations.Add(new LiveTranscriptTranslationRecord
        {
            TranscriptDedupeKey = joke.DedupeKey,
            ChannelSlug = "alpha",
            StartedAt = joke.StartedAt,
            EndedAt = joke.EndedAt,
            AudioStartSeconds = joke.AudioStartSeconds,
            SourceLanguage = "de",
            TargetLanguage = "pl",
            Text = "co za zart",
            SegmentsJson = "[]",
            SegmentCount = 0,
            Provider = "stub",
            TranslatedAt = joke.EndedAt.AddSeconds(3),
        });

        await db.SaveChangesAsync();
    }

    private static LiveTranscriptRecord Speech(string slug, int start, int seconds, string text, string language = "pl", float confidence = 0.9f)
    {
        var startedAt = T0.AddSeconds(start);
        var audioStart = 1000 + start;
        return new LiveTranscriptRecord
        {
            DedupeKey = LiveTranscriptRecord.BuildDedupeKey(slug, startedAt, audioStart),
            ChannelSlug = slug,
            KickChannelId = "1",
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(seconds),
            AudioStartSeconds = audioStart,
            AudioSeconds = seconds,
            Text = text,
            Language = language,
            Confidence = confidence,
            SegmentsJson = "[]",
            SegmentCount = 0,
            Model = "test",
            TranscribedAt = startedAt.AddSeconds(seconds + 2),
            ReceivedAt = startedAt.AddSeconds(seconds + 12),
        };
    }

    private static ChatMessageRecord Chat(string slug, string id, int t, string userId, string username, string content,
        (string MessageId, string UserId, string Username)? replyTo = null) => new()
    {
        MessageId = id,
        Source = ChatSource.Realtime,
        SourceId = "rt-" + id,
        BroadcasterUserId = "1000",
        ChannelSlug = slug,
        SenderUserId = userId,
        SenderUsername = username,
        Content = content,
        CreatedAt = T0.AddSeconds(t),
        ReplyToMessageId = replyTo?.MessageId,
        ReplyToUserId = replyTo?.UserId,
        ReplyToUsername = replyTo?.Username,
    };
}
