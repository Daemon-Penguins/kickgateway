using TailoredApps.KickGateway.Api.LiveFeed;
using TailoredApps.KickGateway.Contracts.Realtime;
using TailoredApps.TwitchGateway.Contracts.Chat;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// The in-memory tap behind the public landing page: last-10 ring, dedupe of the same chat message seen on two
/// transports, per-minute load buckets and the display helpers (badge labels, emote rewriting).
/// </summary>
public class HomeFeedTests
{
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HomeFeedEntry Chat(string id, string text = "hi", string platform = "kick") =>
        new(HomeFeedKind.Chat, platform, "alpha", "bob", ["mod"], text, "#ff0000", HomeFeed.ChatKey(platform, id));

    private static HomeFeedEntry Speech(string key) =>
        new(HomeFeedKind.Transcript, "kick", "alpha", "alpha", ["pl"], "dzien dobry", null, key);

    [Fact]
    public void Append_keeps_the_newest_ten_newest_first()
    {
        var feed = new HomeFeed(new FakeClock());
        for (var i = 1; i <= 13; i++) feed.Append(Chat($"m{i}", $"text {i}"));

        var items = feed.Snapshot();
        Assert.Equal(HomeFeed.Capacity, items.Count);
        Assert.Equal("text 13", items[0].Entry.Text);
        Assert.Equal("text 4", items[^1].Entry.Text);
        Assert.True(items[0].Id > items[1].Id);
    }

    [Fact]
    public void Append_collapses_the_same_message_from_webhook_and_realtime()
    {
        var feed = new HomeFeed(new FakeClock());

        Assert.NotNull(feed.Append(Chat("42")));                 // webhook delivery
        Assert.Null(feed.Append(Chat("42", "same message")));    // Pusher delivery of the same Kick message
        Assert.NotNull(feed.Append(Chat("42", platform: "twitch"))); // a Twitch id is a different key space

        Assert.Equal(2, feed.Snapshot().Count);
        Assert.Equal(2, feed.Load(1)[0].Messages);
    }

    [Fact]
    public void Entries_without_a_key_are_never_deduped()
    {
        var feed = new HomeFeed(new FakeClock());
        Assert.Equal("", HomeFeed.ChatKey("kick", " "));
        var entry = Chat("");
        Assert.NotNull(feed.Append(entry));
        Assert.NotNull(feed.Append(entry));
        Assert.Equal(2, feed.Snapshot().Count);
    }

    [Fact]
    public void Load_buckets_chat_and_speech_per_minute_oldest_first_with_zero_gaps()
    {
        var clock = new FakeClock();
        var feed = new HomeFeed(clock);

        clock.Now = new DateTimeOffset(2026, 10, 19, 12, 0, 10, TimeSpan.Zero);
        feed.Append(Chat("a"));
        feed.Append(Chat("b"));
        feed.Append(Speech("s1"));

        clock.Now = new DateTimeOffset(2026, 10, 19, 12, 2, 59, TimeSpan.Zero);
        feed.Append(Chat("c"));

        clock.Now = new DateTimeOffset(2026, 10, 19, 12, 3, 5, TimeSpan.Zero);
        var load = feed.Load(4);

        Assert.Equal(4, load.Count);
        Assert.Equal(new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc), load[0].Minute);
        Assert.Equal((2, 1), (load[0].Messages, load[0].Transcripts));
        Assert.Equal((0, 0), (load[1].Messages, load[1].Transcripts));
        Assert.Equal((1, 0), (load[2].Messages, load[2].Transcripts));
        Assert.Equal(new DateTime(2026, 10, 19, 12, 3, 0, DateTimeKind.Utc), load[3].Minute); // the running minute
        Assert.Equal((0, 0), (load[3].Messages, load[3].Transcripts));
        Assert.Empty(feed.Load(0));
    }

    [Fact]
    public void Load_forgets_minutes_older_than_the_retention()
    {
        var clock = new FakeClock();
        var feed = new HomeFeed(clock);
        feed.Append(Chat("old"));

        clock.Now = clock.Now.AddMinutes(HomeFeed.BucketRetentionMinutes + 5);
        feed.Append(Chat("new"));   // a new bucket triggers the prune

        var all = feed.Load(HomeFeed.BucketRetentionMinutes + 10);
        Assert.Equal(1, all.Sum(l => l.Messages));
        Assert.Equal(1, all[^1].Messages);
    }

    [Fact]
    public void Subscribers_get_each_accepted_item_until_disposed()
    {
        var feed = new HomeFeed(new FakeClock());
        var seen = new List<string>();
        var sub = feed.Subscribe(i => seen.Add(i.Entry.Text));
        feed.Subscribe(_ => throw new InvalidOperationException("one bad circuit"));

        feed.Append(Chat("1", "one"));
        feed.Append(Chat("1", "dup"));
        sub.Dispose();
        feed.Append(Chat("2", "two"));

        Assert.Equal(["one"], seen);
    }

    [Theory]
    [InlineData("Moderator", 0, "mod")]
    [InlineData("subscriber", 14, "sub 14")]
    [InlineData("Subscriber", 0, "sub")]
    [InlineData("sub_gifter", 25, "gifter")]
    [InlineData("Sub Gifter", 0, "gifter")]
    [InlineData("OG", 0, "og")]
    [InlineData("vip", 0, "vip")]
    [InlineData("broadcaster", 0, "broadcaster")]
    [InlineData("premium", 0, "prime")]
    [InlineData("bits-leader", 0, "bits")]
    [InlineData("some|weird:badge name", 0, "someweirdbadge_n")]
    [InlineData("  ", 3, "")]
    public void Badge_names_from_any_platform_become_short_labels(string name, int count, string expected) =>
        Assert.Equal(expected, ChatBadgeLabels.Short(name, count));

    [Fact]
    public void Badges_are_collected_per_platform_deduped_and_capped()
    {
        Assert.Equal(["mod", "sub"], ChatBadgeLabels.FromKick(["Moderator", "Subscriber", "", "moderator"]));

        var realtime = ChatBadgeLabels.FromKick(new[]
        {
            new RealtimeChatBadge("subscriber", "Subscriber", 14),
            new RealtimeChatBadge("", "OG", 0),
            new RealtimeChatBadge("vip", "VIP", 0),
            new RealtimeChatBadge("founder", "Founder", 0),
            new RealtimeChatBadge("verified", "Verified", 0),
            new RealtimeChatBadge("sub_gifter", "Sub Gifter", 50),
        });
        Assert.Equal(["sub 14", "og", "vip", "founder"], realtime);   // capped at MaxBadges

        var twitch = ChatBadgeLabels.FromTwitch(new[]
        {
            new TwitchChatBadge("moderator", "1", ""),
            new TwitchChatBadge("subscriber", "3012", "37"),
            new TwitchChatBadge("premium", "1", ""),
        });
        Assert.Equal(["mod", "sub 37", "prime"], twitch);

        Assert.Empty(ChatBadgeLabels.FromKick((string[]?)null));
    }

    [Fact]
    public void Display_text_rewrites_kick_emotes_collapses_whitespace_and_trims()
    {
        Assert.Equal("gg :KEKW: wp", HomeFeedText.Display("gg [emote:37225:KEKW]   wp\n"));
        Assert.Equal("", HomeFeedText.Display("   "));

        var longText = new string('x', HomeFeedText.MaxLength + 50);
        var shown = HomeFeedText.Display(longText);
        Assert.Equal(HomeFeedText.MaxLength, shown.Length);
        Assert.EndsWith("…", shown);
    }

    [Theory]
    [InlineData("#1E90FF", "#1E90FF")]
    [InlineData("#fff", "#fff")]
    [InlineData("red", null)]
    [InlineData("#12345", null)]
    [InlineData("#fff;background:url(x)", null)]
    [InlineData(null, null)]
    public void Username_colour_is_used_only_when_it_is_a_hex_colour(string? input, string? expected) =>
        Assert.Equal(expected, HomeFeedText.CssColor(input));
}
