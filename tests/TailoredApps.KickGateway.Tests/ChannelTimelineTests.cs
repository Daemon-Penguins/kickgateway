using TailoredApps.KickGateway.Api.Analytics;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>Pure parts of the channel timeline: include parsing, the keyset cursor and the per-kind merge.</summary>
public class ChannelTimelineTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 20, 0, 0, DateTimeKind.Utc);

    private static TimelineItem Speech(long id, int t, string text = "...") =>
        new(TimelineKinds.Speech, T0.AddSeconds(t), id.ToString(), "alpha", text, EndedAt: T0.AddSeconds(t + 10), Language: "pl", Confidence: 0.9f);

    private static TimelineItem Chat(string id, int t, string who = "alice") =>
        new(TimelineKinds.Chat, T0.AddSeconds(t), id, who, "msg " + id, UserId: "1");

    private static TimelineItem Event(string id, int t) =>
        new(TimelineKinds.Event, T0.AddSeconds(t), id, "bob", "", UserId: "2", EventKind: "follow");

    [Fact]
    public void Include_defaults_to_all_and_accepts_synonyms()
    {
        Assert.True(TimelineKinds.TryParseInclude(null, out var all, out _));
        Assert.Equal(new[] { "speech", "event", "chat" }, TimelineKinds.All.Where(all.Contains));

        Assert.True(TimelineKinds.TryParseInclude(" Chat , transcripts", out var some, out _));
        Assert.Equal(new[] { "speech", "chat" }, TimelineKinds.All.Where(some.Contains));

        Assert.False(TimelineKinds.TryParseInclude("chat,bogus", out _, out var error));
        Assert.Contains("bogus", error);
    }

    [Fact]
    public void Cursor_round_trips_and_rejects_garbage()
    {
        var item = Chat("msg|with|pipes", 5);
        var cursor = TimelineCursor.Of(item).Format();
        Assert.DoesNotContain("=", cursor);

        Assert.True(TimelineCursor.TryParse(cursor, out var parsed));
        Assert.Equal(item.At, parsed.At);
        Assert.Equal(DateTimeKind.Utc, parsed.At.Kind);
        Assert.Equal(TimelineKinds.Rank(TimelineKinds.Chat), parsed.Rank);
        Assert.Equal("msg|with|pipes", parsed.Id);

        var speech = TimelineCursor.Of(Speech(42, 1)).Format();
        Assert.True(TimelineCursor.TryParse(speech, out var s));
        Assert.Equal(42, s.SpeechId);

        Assert.False(TimelineCursor.TryParse(null, out _));
        Assert.False(TimelineCursor.TryParse("not base64 !!", out _));
        Assert.False(TimelineCursor.TryParse(Convert.ToBase64String("1|0|not-a-number"u8.ToArray()), out _)); // speech id must be numeric
        Assert.False(TimelineCursor.TryParse(Convert.ToBase64String("1|9|x"u8.ToArray()), out _));            // unknown rank
    }

    [Fact]
    public void Merge_orders_by_time_then_speech_event_chat_and_keeps_source_order_on_ties()
    {
        var speech = new[] { Speech(1, 0), Speech(2, 20) };
        var events = new[] { Event("e1", 25) };
        var chat = new[] { Chat("m3", 20), Chat("m1", 25), Chat("m2", 25), Chat("m4", 200) };

        var (items, next) = TimelineMerge.Page([speech, events, chat], 100);

        Assert.Equal(new[] { "1", "2", "m3", "e1", "m1", "m2", "m4" }, items.Select(i => i.Id));
        Assert.Null(next);
    }

    [Fact]
    public void Merge_pages_and_reports_a_cursor_only_when_something_is_left()
    {
        var speech = new[] { Speech(1, 0), Speech(2, 20), Speech(3, 40) };
        var chat = new[] { Chat("m1", 10), Chat("m2", 30) };

        var (page, next) = TimelineMerge.Page([speech, chat], 3);
        Assert.Equal(new[] { "1", "m1", "2" }, page.Select(i => i.Id));
        Assert.NotNull(next);
        Assert.True(TimelineCursor.TryParse(next, out var cursor));
        Assert.Equal(T0.AddSeconds(20), cursor.At);
        Assert.Equal(TimelineKinds.Rank(TimelineKinds.Speech), cursor.Rank);
        Assert.Equal("2", cursor.Id);

        // Exactly 'limit' items left in total and nothing beyond: no cursor.
        var (rest, none) = TimelineMerge.Page([new[] { Speech(3, 40) }, new[] { Chat("m2", 30) }], 2);
        Assert.Equal(new[] { "m2", "3" }, rest.Select(i => i.Id));
        Assert.Null(none);

        var (empty, noCursor) = TimelineMerge.Page([Array.Empty<TimelineItem>()], 5);
        Assert.Empty(empty);
        Assert.Null(noCursor);
    }
}
