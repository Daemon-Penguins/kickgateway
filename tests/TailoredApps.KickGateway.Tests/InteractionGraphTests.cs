using TailoredApps.KickGateway.Api.Analytics;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class InteractionGraphTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoMentions = new Dictionary<string, IReadOnlyList<string>>();

    private static GraphMessage Msg(string id, string sender, int second, string? replyTo = null) =>
        new(id, sender, "user" + sender, T0.AddSeconds(second), replyTo, replyTo is null ? null : "user" + replyTo);

    [Fact]
    public void Replies_and_mentions_become_weighted_directed_edges()
    {
        var messages = new[]
        {
            Msg("m1", "1", 0),
            Msg("m2", "2", 100, replyTo: "1"),          // 2 → 1 reply
            Msg("m3", "2", 200, replyTo: "1"),          // 2 → 1 reply, also @user1 (same act, not double counted)
            Msg("m4", "1", 300),                        // mentions @user3 → resolves to id 3
            Msg("m5", "3", 400),                        // mentions @ghost (never chatted)
        };
        var mentions = new Dictionary<string, IReadOnlyList<string>>
        {
            ["m3"] = new[] { "user1" },
            ["m4"] = new[] { "user3" },
            ["m5"] = new[] { "ghost" },
        };
        var g = InteractionGraph.Build(messages, mentions, Array.Empty<GraphGift>(), new GraphOptions { IncludeProximity = false });

        var e21 = Assert.Single(g.Edges, e => e.From == "2" && e.To == "1");
        Assert.Equal(2, e21.Replies);
        Assert.Equal(0, e21.Mentions);
        Assert.Equal(6, e21.Weight);

        var e13 = Assert.Single(g.Edges, e => e.From == "1" && e.To == "3");
        Assert.Equal(1, e13.Mentions);
        Assert.Equal(2, e13.Weight);

        var ghost = Assert.Single(g.Nodes, n => n.Key == "@ghost");
        Assert.Null(ghost.UserId);
        Assert.Equal(0, ghost.Messages);
        Assert.Equal(1, ghost.MentionsReceived);

        var n1 = Assert.Single(g.Nodes, n => n.Key == "1");
        Assert.Equal(2, n1.RepliesReceived);
        Assert.Equal(6, n1.InWeight);
        Assert.Equal(5, g.Messages);
        Assert.Equal(3, g.Chatters);
    }

    [Fact]
    public void Proximity_only_fires_in_slow_chat_and_splits_between_recent_speakers()
    {
        var slow = new[] { Msg("a", "1", 0), Msg("b", "2", 5), Msg("c", "3", 10) };
        var g = InteractionGraph.Build(slow, NoMentions, Array.Empty<GraphGift>(), new GraphOptions());
        Assert.Equal(1.0, Assert.Single(g.Edges, e => e.From == "2" && e.To == "1").Proximity);
        // "3" answers into a window where both 1 and 2 spoke → half each.
        Assert.Equal(0.5, Assert.Single(g.Edges, e => e.From == "3" && e.To == "2").Proximity);
        Assert.Equal(0.5, Assert.Single(g.Edges, e => e.From == "3" && e.To == "1").Proximity);

        // Busy chat: 4 other speakers inside the window → no proximity edge for the 5th.
        var busy = new[] { Msg("a", "1", 0), Msg("b", "2", 1), Msg("c", "3", 2), Msg("d", "4", 3), Msg("e", "5", 4) };
        var gb = InteractionGraph.Build(busy, NoMentions, Array.Empty<GraphGift>(), new GraphOptions());
        Assert.DoesNotContain(gb.Edges, e => e.From == "5");

        // Outside the window → nothing; explicit reply suppresses proximity.
        var gap = new[] { Msg("a", "1", 0), Msg("b", "2", 60), Msg("c", "1", 65, replyTo: "2") };
        var gg = InteractionGraph.Build(gap, NoMentions, Array.Empty<GraphGift>(), new GraphOptions());
        Assert.DoesNotContain(gg.Edges, e => e.From == "2");
        var back = Assert.Single(gg.Edges, e => e.From == "1" && e.To == "2");
        Assert.Equal(1, back.Replies);
        Assert.Equal(0, back.Proximity);
    }

    [Fact]
    public void Gifts_add_edges_and_communities_split_two_cliques_joined_by_a_bridge()
    {
        // Clique A = {1,2,3}, clique B = {4,5,6}; a single weak tie 3 → 4.
        var messages = new List<GraphMessage>();
        var n = 0;
        void Reply(string from, string to, int times)
        {
            for (var i = 0; i < times; i++) messages.Add(Msg($"m{n}", from, 1000 * n++, replyTo: to));
        }
        foreach (var (a, b) in new[] { ("1", "2"), ("2", "3"), ("3", "1"), ("4", "5"), ("5", "6"), ("6", "4") })
        {
            Reply(a, b, 3);
            Reply(b, a, 3);
        }
        Reply("3", "4", 1);
        var gifts = new[] { new GraphGift("1", "user1", "2", "user2", 2) };

        var g = InteractionGraph.Build(messages, NoMentions, gifts, new GraphOptions { IncludeProximity = false });

        Assert.Equal(2, Assert.Single(g.Edges, e => e.From == "1" && e.To == "2").Gifts);
        Assert.Equal(2, g.Communities.Count);
        var c = g.Nodes.ToDictionary(x => x.Key, x => x.Community);
        Assert.Equal(c["1"], c["2"]);
        Assert.Equal(c["1"], c["3"]);
        Assert.Equal(c["4"], c["5"]);
        Assert.Equal(c["4"], c["6"]);
        Assert.NotEqual(c["1"], c["4"]);
        Assert.True(g.Modularity > 0.3, $"modularity {g.Modularity}");

        // The bridge ends have ties into both communities; clique-internal nodes don't.
        Assert.True(g.Nodes.Single(x => x.Key == "3").Participation > 0);
        Assert.Equal(0, g.Nodes.Single(x => x.Key == "2").Participation);
        Assert.All(g.Communities, com => Assert.True(com.Cohesion > 0.9));
        Assert.Equal(12 / 13.0, g.Reciprocity, 3); // every edge but the one-way bridge is mutual
    }

    [Fact]
    public void Empty_input_is_an_empty_graph()
    {
        var g = InteractionGraph.Build(Array.Empty<GraphMessage>(), NoMentions, Array.Empty<GraphGift>(), new GraphOptions());
        Assert.Empty(g.Nodes);
        Assert.Empty(g.Edges);
        Assert.Empty(g.Communities);
        Assert.Equal(0, g.Modularity);
    }

    [Fact]
    public void Louvain_is_deterministic_and_handles_isolated_pairs()
    {
        var edges = new List<(int, int, double)> { (0, 1, 1), (2, 3, 1) };
        var (m1, q1) = Louvain.Detect(4, edges);
        var (m2, q2) = Louvain.Detect(4, edges);
        Assert.Equal(m1, m2);
        Assert.Equal(q1, q2);
        Assert.Equal(m1[0], m1[1]);
        Assert.Equal(m1[2], m1[3]);
        Assert.NotEqual(m1[0], m1[2]);
        Assert.Equal(0.5, q1, 3);
    }
}

public class ChatTextStatsTests
{
    [Fact]
    public void Computes_shares_terms_and_emotes()
    {
        var s = ChatTextStats.Compute(new[]
        {
            "[emote:1:KEKW] [emote:1:KEKW]",
            "!points",
            "is this LIVE ALREADY?",
            "check https://example.com/clip",
            "great stream great vibes",
        });

        Assert.Equal(5, s.Messages);
        Assert.Equal(0.2, s.EmoteShare);
        Assert.Equal(0.2, s.EmoteOnlyShare);
        Assert.Equal(0.2, s.CommandShare);
        Assert.Equal(0.2, s.QuestionShare);
        Assert.Equal(0.2, s.LinkShare);
        Assert.Equal(new TermCount("KEKW", 2), Assert.Single(s.TopEmotes));
        Assert.Equal(new TermCount("great", 2), s.TopTerms[0]);
        Assert.DoesNotContain(s.TopTerms, t => t.Term.Contains("example") || t.Term == "points" || t.Term == "this");
    }

    [Theory]
    [InlineData("HELLO THERE", true)]
    [InlineData("OK", false)]
    [InlineData("Hello There", false)]
    public void Shouting(string text, bool expected) => Assert.Equal(expected, ChatTextStats.IsShouting(text));

    [Fact]
    public void Empty_input() => Assert.Equal(0, ChatTextStats.Compute(Array.Empty<string>()).Messages);
}

public class AnalyticsTimeTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("7d", "2026-09-17T12:00:00Z")]
    [InlineData("12h", "2026-09-24T00:00:00Z")]
    [InlineData("30m", "2026-09-24T11:30:00Z")]
    [InlineData("2w", "2026-09-10T12:00:00Z")]
    [InlineData("2026-09-01", "2026-09-01T00:00:00Z")]
    [InlineData("2026-09-01T20:00:00+02:00", "2026-09-01T18:00:00Z")]
    public void Parses_relative_and_absolute(string input, string expected)
    {
        Assert.True(AnalyticsTime.TryParse(input, Now, out var value));
        Assert.Equal(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal), value);
    }

    [Fact]
    public void Window_defaults_all_and_validation()
    {
        Assert.True(AnalyticsTime.TryResolveWindow(null, null, 7, Now, out var w, out _));
        Assert.Equal(Now.AddDays(-7), w.From);
        Assert.Equal(Now, w.To);

        Assert.True(AnalyticsTime.TryResolveWindow("all", null, 7, Now, out var all, out _));
        Assert.Null(all.From);

        Assert.True(AnalyticsTime.TryResolveWindow(null, null, null, Now, out var unbounded, out _));
        Assert.Null(unbounded.From);

        Assert.False(AnalyticsTime.TryResolveWindow("yesterday-ish", null, 7, Now, out _, out var err));
        Assert.Contains("from", err);
        Assert.False(AnalyticsTime.TryResolveWindow("1h", "2h", 7, Now, out _, out _)); // from after to
    }

    [Fact]
    public void Api_key_comparison()
    {
        Assert.True(AnalyticsAuth.KeysMatch("abc", "abc"));
        Assert.False(AnalyticsAuth.KeysMatch("abc", "abd"));
        Assert.False(AnalyticsAuth.KeysMatch("abc", "abcd"));
    }
}
