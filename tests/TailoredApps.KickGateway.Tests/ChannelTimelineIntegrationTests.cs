using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// <see cref="TimelineQueries"/> against a real SQL Server (testcontainer, real migrations): speech slices,
/// chatter events and chat messages of one channel merged by stream time, with exact keyset paging through
/// timestamp ties. Skipped without Docker.
/// </summary>
public class ChannelTimelineIntegrationTests(ChatAnalyticsDatabase fixture) : IClassFixture<ChatAnalyticsDatabase>
{
    private static readonly DateTime T0 = new(2026, 10, 11, 20, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<string> AllKinds = TimelineKinds.All.ToHashSet(StringComparer.Ordinal);

    /// <summary>Speech rows are identified by their text (ids are identity values); everything else by id.</summary>
    private static IEnumerable<string> Keys(TimelinePage page) => page.Items.Select(i => i.Kind == TimelineKinds.Speech ? i.Text : i.Id);

    [SkippableFact]
    public async Task Timeline_merges_speech_events_and_chat_by_stream_time_and_pages_exactly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var cs = await fixture.CreateDatabaseAsync("timeline");
        await using var db = ChatAnalyticsDatabase.Open(cs);
        await SeedAsync(db);

        var window = new AnalyticsWindow(T0, T0.AddSeconds(300));
        var expected = new[] { "intro", "hello chat", "what do you think", "m3", "e1", "m1", "m2", "e2", "mumble", "m4" };

        var full = await TimelineQueries.PageAsync(db, "alpha", window, AllKinds, 0, null, 100, default);
        Assert.Equal(expected, Keys(full));
        Assert.Null(full.NextCursor);
        Assert.Equal(new[] { "speech", "event", "chat" }, full.Kinds);

        // Any page size walks the same sequence, including through the ties at T0+20 and T0+25.
        foreach (var size in new[] { 1, 2, 3, 7 })
        {
            var seen = new List<string>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var page = await TimelineQueries.PageAsync(db, "alpha", window, AllKinds, 0, cursor, size, default);
                Assert.True(page.Items.Count <= size);
                seen.AddRange(Keys(page));
                cursor = page.NextCursor;
                Assert.True(++pages <= expected.Length + 1, "paging did not terminate");
            } while (cursor is not null);
            Assert.Equal(expected, seen);
        }

        // Kind-specific fields and the translation joined onto its slice.
        var s2 = full.Items.Single(i => i.Text == "what do you think");
        Assert.Equal("alpha", s2.Who);
        Assert.Equal("de", s2.Language);
        Assert.Equal(T0.AddSeconds(40), s2.EndedAt);
        Assert.Equal("co o tym myslisz", s2.Translations!["pl"]);
        Assert.Null(full.Items.Single(i => i.Text == "hello chat").Translations);

        var e1 = full.Items.Single(i => i.Id == "e1");
        Assert.Equal("follow", e1.EventKind);
        Assert.Equal("Carol", e1.Who);
        Assert.Equal("3", e1.UserId);
        Assert.Null(e1.Amount);
        var e2 = full.Items.Single(i => i.Id == "e2");
        Assert.Equal("message_deleted", e2.EventKind);
        Assert.Equal("ai", e2.Text);

        var m2 = full.Items.Single(i => i.Id == "m2");
        Assert.Equal("Bob", m2.Who);
        Assert.Equal("2", m2.UserId);
        Assert.Equal("m3", m2.ReplyToMessageId);
        Assert.Equal("Alice", m2.ReplyToUsername);
    }

    [SkippableFact]
    public async Task Timeline_filters_by_kind_confidence_and_window()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var cs = await fixture.CreateDatabaseAsync("timeline_filters");
        await using var db = ChatAnalyticsDatabase.Open(cs);
        await SeedAsync(db);

        var window = new AnalyticsWindow(T0, T0.AddSeconds(300));

        Assert.True(TimelineKinds.TryParseInclude("chat", out var chatOnly, out _));
        var chat = await TimelineQueries.PageAsync(db, "alpha", window, chatOnly, 0, null, 100, default);
        Assert.Equal(new[] { "m3", "m1", "m2", "m4" }, Keys(chat));
        Assert.Equal(new[] { "chat" }, chat.Kinds);

        Assert.True(TimelineKinds.TryParseInclude("speech,events", out var noChat, out _));
        var confident = await TimelineQueries.PageAsync(db, "alpha", window, noChat, 0.5f, null, 100, default);
        Assert.Equal(new[] { "intro", "hello chat", "what do you think", "e1", "e2" }, Keys(confident));

        // 'at' style window [T0+20, T0+30): the slice that ENDS at T0+20 still overlaps (grace), the intro does not.
        var moment = await TimelineQueries.PageAsync(db, "alpha", new AnalyticsWindow(T0.AddSeconds(20), T0.AddSeconds(30)), AllKinds, 0, null, 100, default);
        Assert.Equal(new[] { "hello chat", "what do you think", "m3", "e1", "m1", "m2" }, Keys(moment));

        // Unbounded start ('all') reaches the old slice; the other channel never leaks in.
        var all = await TimelineQueries.PageAsync(db, "alpha", new AnalyticsWindow(null, T0.AddSeconds(300)), AllKinds, 0, null, 100, default);
        Assert.Equal("old", Keys(all).First());
        Assert.DoesNotContain(all.Items, i => i.Who == "beta" || i.Id.StartsWith("b", StringComparison.Ordinal));

        var beta = await TimelineQueries.PageAsync(db, "beta", window, AllKinds, 0, null, 100, default);
        Assert.Equal(new[] { "beta speech", "b1", "bm1" }, Keys(beta));
    }

    private static async Task SeedAsync(KickGatewayDbContext db)
    {
        db.LiveTranscripts.AddRange(
            Speech("alpha", -300, 20, "old"),
            Speech("alpha", -30, 35, "intro"),
            Speech("alpha", 0, 20, "hello chat"),
            Speech("alpha", 20, 20, "what do you think", language: "de"),
            Speech("alpha", 100, 15, "mumble", confidence: 0.3f),
            Speech("beta", 10, 10, "beta speech"));

        db.ChatMessages.AddRange(
            Chat("alpha", "m3", 20, "1", "Alice", "first!"),
            Chat("alpha", "m1", 25, "1", "Alice", "hello"),
            Chat("alpha", "m2", 25, "2", "Bob", "yes", replyTo: ("m3", "1", "Alice")),
            Chat("alpha", "m4", 200, "2", "Bob", "late"),
            Chat("alpha", "m5", 400, "2", "Bob", "outside the window"),
            Chat("beta", "bm1", 30, "6", "Frank", "beta chat"));

        db.ChatterEvents.AddRange(
            Event("alpha", "e1", ChatterEventKind.Follow, 25, "3", "Carol"),
            Event("alpha", "e2", ChatterEventKind.MessageDeleted, 45, "1", "Alice", detail: "ai", refId: "m1"),
            Event("beta", "b1", ChatterEventKind.Follow, 12, "6", "Frank"));

        var translated = db.LiveTranscripts.Local.Single(t => t.Text == "what do you think");
        db.LiveTranscriptTranslations.Add(new LiveTranscriptTranslationRecord
        {
            TranscriptDedupeKey = translated.DedupeKey,
            ChannelSlug = "alpha",
            StartedAt = translated.StartedAt,
            EndedAt = translated.EndedAt,
            AudioStartSeconds = translated.AudioStartSeconds,
            SourceLanguage = "de",
            TargetLanguage = "pl",
            Text = "co o tym myslisz",
            SegmentsJson = "[]",
            SegmentCount = 0,
            Provider = "stub",
            TranslatedAt = translated.EndedAt.AddSeconds(3),
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

    private static ChatterEvent Event(string slug, string id, ChatterEventKind kind, int t, string userId, string username,
        string? detail = null, string? refId = null) => new()
    {
        Id = id,
        Source = ChatSource.Realtime,
        SourceId = "rt-" + id,
        Kind = kind,
        ChannelSlug = slug,
        UserId = userId,
        Username = username,
        Detail = detail,
        RefId = refId,
        OccurredAt = T0.AddSeconds(t),
    };
}
