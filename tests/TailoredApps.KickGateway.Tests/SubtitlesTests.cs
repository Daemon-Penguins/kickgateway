using System.Text.Json;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subtitles;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// The live-subtitles site: per-channel backlog + fan-out with resumable ids, slug hygiene, SSE framing.
/// </summary>
public class SubtitlesTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static LiveTranscript Transcript(string slug, double audioStart, string text, string language = "pl") => new()
    {
        BroadcasterSlug = slug,
        KickChannelId = "42",
        StartedAt = T0.AddSeconds(audioStart),
        EndedAt = T0.AddSeconds(audioStart + 12),
        AudioStartSeconds = audioStart,
        AudioSeconds = 12,
        Text = text,
        Language = language,
        DetectedLanguage = language,
        LanguageProbability = 0.91f,
        Confidence = 0.8f,
        Segments = [new LiveTranscriptSegment { StartedAt = T0.AddSeconds(audioStart), EndedAt = T0.AddSeconds(audioStart + 12), Text = text, Confidence = 0.8f }],
        Model = "LargeV3Turbo/Q5_0",
        TranscribedAt = T0.AddSeconds(audioStart + 13),
        ProcessingSeconds = 0.9,
    };

    private static (TranscriptFeed feed, FakeTime time) Feed(int backlogSeconds = 180, int maxItems = 60)
    {
        var time = new FakeTime(new DateTimeOffset(T0.AddSeconds(15), TimeSpan.Zero));
        return (new TranscriptFeed(new SubtitlesOptions { BacklogSeconds = backlogSeconds, MaxBacklogItems = maxItems }, time), time);
    }

    // ---- slugs / options ----

    [Theory]
    [InlineData("chef_jan", "chef_jan")]
    [InlineData("  Chef_Jan ", "chef_jan")]
    [InlineData("abc-123", "abc-123")]
    public void Slugs_are_normalized_to_lowercase(string raw, string expected)
    {
        Assert.True(SubtitlesOptions.TryNormalizeSlug(raw, out var slug));
        Assert.Equal(expected, slug);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("<script>")]
    [InlineData("..")]
    public void Unsafe_slugs_are_rejected(string raw) => Assert.False(SubtitlesOptions.TryNormalizeSlug(raw, out _));

    [Fact]
    public void Allowlist_is_optional_and_case_insensitive()
    {
        var any = new SubtitlesOptions();
        Assert.True(any.Allows("whoever"));

        var some = new SubtitlesOptions { Channels = ["Chef_Jan", " trazer "] };
        Assert.Equal(["chef_jan", "trazer"], some.NormalizedChannels);
        Assert.True(some.Allows("chef_jan"));
        Assert.False(some.Allows("whoever"));
        some.Validate();
    }

    [Fact]
    public void Queue_name_is_unique_per_process_unless_configured()
    {
        var a = new SubtitlesOptions();
        var b = new SubtitlesOptions();
        Assert.StartsWith("subtitles-", a.EffectiveQueueName);
        Assert.NotEqual(a.EffectiveQueueName, b.EffectiveQueueName);
        Assert.Equal(a.EffectiveQueueName, a.EffectiveQueueName); // stable within the instance
        Assert.Equal("my-queue", new SubtitlesOptions { QueueName = " my-queue " }.EffectiveQueueName);
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SubtitlesOptions { Channels = ["bad.slug"] }.Validate());
        Assert.Throws<ArgumentException>(() => new SubtitlesOptions { KeepAliveSeconds = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new SubtitlesOptions { BacklogSeconds = -1 }.Validate());
    }

    // ---- feed ----

    [Fact]
    public void Publish_assigns_per_channel_ids_and_measures_lag()
    {
        var (feed, time) = Feed();
        var a1 = feed.Publish(Transcript("Alpha", 0, "one"))!;
        var a2 = feed.Publish(Transcript("alpha", 12, "two"))!;
        var b1 = feed.Publish(Transcript("beta", 0, "uno"))!;

        Assert.Equal("alpha", a1.Slug);                 // slug normalized
        Assert.Equal(1, a1.Id);
        Assert.Equal(2, a2.Id);
        Assert.Equal(1, b1.Id);                          // ids are per channel
        Assert.Equal(3, a1.LagSeconds);                  // received at T0+15, audio ended at T0+12
        Assert.Equal(0, a2.LagSeconds);                  // never negative (ended at T0+24, received T0+15)
        Assert.Equal("pl", a1.Language);
        Assert.Null(a1.Translations);                    // slot for the translation layer, empty today
        Assert.Equal(["alpha", "beta"], feed.ActiveSlugs);
    }

    [Fact]
    public void Blank_or_unusable_transcripts_are_ignored()
    {
        var (feed, _) = Feed();
        Assert.Null(feed.Publish(Transcript("alpha", 0, "   ")));
        Assert.Null(feed.Publish(Transcript("bad slug", 0, "text")));
        Assert.Empty(feed.ActiveSlugs);
    }

    [Fact]
    public void Backlog_is_bounded_by_count_and_age()
    {
        var (feed, time) = Feed(backlogSeconds: 60, maxItems: 3);
        for (var i = 0; i < 5; i++) feed.Publish(Transcript("alpha", i * 12, $"line {i}"));
        Assert.Equal(["line 2", "line 3", "line 4"], feed.Backlog("alpha").Select(e => e.Text));

        time.Now = time.Now.AddSeconds(61); // everything so far is older than the window
        Assert.Empty(feed.Backlog("alpha"));

        feed.Publish(Transcript("alpha", 60, "fresh"));
        Assert.Equal(["fresh"], feed.Backlog("alpha").Select(e => e.Text));
    }

    [Fact]
    public void Backlog_resumes_after_an_id()
    {
        var (feed, _) = Feed();
        for (var i = 1; i <= 4; i++) feed.Publish(Transcript("alpha", i * 12, $"line {i}"));
        Assert.Equal(["line 3", "line 4"], feed.Backlog("alpha", afterId: 2).Select(e => e.Text));
        Assert.Empty(feed.Backlog("nobody"));
    }

    [Fact]
    public async Task Subscriber_gets_backlog_then_live_events_without_gaps()
    {
        var (feed, _) = Feed();
        feed.Publish(Transcript("alpha", 0, "before"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<string>();
        var enumerator = feed.SubscribeAsync("alpha", afterId: 0, cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync());
        received.Add(enumerator.Current.Text);
        Assert.Equal(1, feed.SubscriberCount("alpha"));

        feed.Publish(Transcript("alpha", 12, "live 1"));
        feed.Publish(Transcript("beta", 0, "other channel")); // must not leak into alpha's stream
        feed.Publish(Transcript("alpha", 24, "live 2"));

        Assert.True(await enumerator.MoveNextAsync());
        received.Add(enumerator.Current.Text);
        Assert.True(await enumerator.MoveNextAsync());
        received.Add(enumerator.Current.Text);
        Assert.Equal(["before", "live 1", "live 2"], received);
        Assert.Equal([1L, 2L, 3L], new[] { 1L, enumerator.Current.Id - 1, enumerator.Current.Id });

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
        Assert.Equal(0, feed.SubscriberCount("alpha"));
    }

    [Fact]
    public async Task Reconnect_with_last_event_id_skips_what_was_already_shown()
    {
        var (feed, _) = Feed();
        for (var i = 1; i <= 3; i++) feed.Publish(Transcript("alpha", i * 12, $"line {i}"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = feed.SubscribeAsync("alpha", afterId: 2, cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("line 3", enumerator.Current.Text);
        cts.Cancel();
        await enumerator.DisposeAsync();
    }

    // ---- SSE framing ----

    [Fact]
    public void Sse_frame_has_id_event_and_single_line_json()
    {
        var (feed, _) = Feed();
        var ev = feed.Publish(Transcript("alpha", 0, "Dobry wieczór\nwszystkim", "de"))!;
        var frame = Sse.Format(ev);

        Assert.StartsWith("id: 1\nevent: transcript\ndata: {", frame);
        Assert.EndsWith("}\n\n", frame);
        var data = frame.Split('\n')[2]["data: ".Length..];
        Assert.DoesNotContain('\n', data); // JSON escapes the newline, so the frame stays well-formed

        using var json = JsonDocument.Parse(data);
        Assert.Equal("alpha", json.RootElement.GetProperty("slug").GetString());
        Assert.Equal("de", json.RootElement.GetProperty("language").GetString());
        Assert.Equal(0.91f, json.RootElement.GetProperty("languageProbability").GetSingle());
        Assert.Equal("Dobry wieczór\nwszystkim", json.RootElement.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("translations").ValueKind);
        Assert.Equal(1, json.RootElement.GetProperty("segments").GetArrayLength());
    }
}
