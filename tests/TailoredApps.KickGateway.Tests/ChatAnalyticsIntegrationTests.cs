using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Auth;
using TailoredApps.KickGateway.Api.Data;
using Testcontainers.MsSql;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// SQL Server in a container with the real migrations applied, seeded with a small but realistic
/// mix of webhook + realtime inbox rows and projected once. Tests are skipped (not failed) when
/// Docker isn't available.
/// </summary>
public sealed class ChatAnalyticsDatabase : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string? _connectionString;

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            SkipReason = $"SQL Server container unavailable ({ex.GetType().Name}: {ex.Message})";
            return;
        }

        _connectionString = await CreateDatabaseAsync("analytics_main");
        await using var db = Open(_connectionString);
        await ChatSeed.SeedAsync(db);
        await ChatSeed.ProjectAllAsync(db);
    }

    public KickGatewayDbContext Open() => Open(_connectionString!);

    public static KickGatewayDbContext Open(string connectionString) =>
        new(new DbContextOptionsBuilder<KickGatewayDbContext>().UseSqlServer(connectionString).Options);

    /// <summary>Fresh, migrated database in the same container.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        var cs = new SqlConnectionStringBuilder(_container!.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
        await using var db = Open(cs);
        await db.Database.MigrateAsync();
        return cs;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

/// <summary>
/// Seed story. Channel <c>alpha</c> has a webhook broadcaster (and is also followed by the realtime
/// listener, so a few frames duplicate webhook data); channel <c>beta</c> is realtime-only.
/// alice(1) ↔ bob(2) talk via replies, carol(3) joins via @mentions, dave(4)/eve(5) chat an hour
/// later (eve spams, gets timed out by bob, her message is deleted). In beta frank(6) and gina(7)
/// talk; frank's mention message is deleted, gina is timed out by bob and gifted a sub by frank.
/// </summary>
public static class ChatSeed
{
    public static readonly DateTime T0 = new(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc);
    public static readonly AnalyticsWindow All = new(null, T0.AddDays(30));
    public static readonly Guid AlphaClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid AlphaAccountId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static readonly (long Id, string Name) Alice = (1, "Alice"), Bob = (2, "Bob"), Carol = (3, "Carol"),
        Dave = (4, "Dave"), Eve = (5, "Eve"), Frank = (6, "Frank"), Gina = (7, "Gina");

    private static readonly object AlphaBroadcaster = new { user_id = 1000, username = "Alpha", channel_slug = "alpha" };

    public static async Task SeedAsync(KickGatewayDbContext db)
    {
        db.ClientApps.Add(new KickClientApp { Id = AlphaClientId, Name = "app-alpha", ClientId = "client-alpha" });
        db.Broadcasters.Add(new KickBroadcasterAccount
        {
            Id = AlphaAccountId, KickClientAppId = AlphaClientId, KickUserId = "1000", Username = "Alpha", ChannelSlug = "alpha",
        });

        db.ReceivedWebhooks.AddRange(
            WhChat("wh-a1", "a1", Alice, 0, "hello everyone"),
            WhChat("wh-a2", "a2", Bob, 10, "hi alice!", reply: ("a1", Alice)),
            // Same chat message delivered again under another client app → one row.
            WhChat("wh-a2-dup", "a2", Bob, 10, "hi alice!", reply: ("a1", Alice)),
            WhChat("wh-a3", "a3", Alice, 20, "hey bob, how are you?", reply: ("a2", Bob)),
            WhChat("wh-a4", "a4", Bob, 30, "great, thanks @carol are you here?", reply: ("a3", Alice)),
            WhChat("wh-a5", "a5", Carol, 40, "yes @bob [emote:1:KEKW]"),
            WhChat("wh-a6", "a6", Dave, 3600, "!points"),
            WhChat("wh-a7", "a7", Eve, 3610, "SPAM SPAM SPAM"),
            WhChat("wh-a8", "a8", Alice, 86400, "carol you missed it", reply: ("a5", Carol)),
            Wh("wh-follow", KickEventTypes.ChannelFollowed, 50, new { broadcaster = AlphaBroadcaster, follower = User(Carol) }),
            Wh("wh-sub", KickEventTypes.SubscriptionNew, 60, new { broadcaster = AlphaBroadcaster, subscriber = User(Alice), duration = 1, created_at = At(60) }),
            Wh("wh-gift", KickEventTypes.SubscriptionGifts, 70, new
            {
                broadcaster = AlphaBroadcaster, gifter = User(Bob), giftees = new[] { User(Alice), User(Carol) }, created_at = At(70),
            }),
            Wh("wh-kicks", KickEventTypes.KicksGifted, 3605, new
            {
                broadcaster = AlphaBroadcaster, sender = User(Dave), gift = new { amount = 100, name = "Hype", message = "gg" }, created_at = At(3605),
            }),
            Wh("wh-ban", KickEventTypes.ModerationBanned, 3620, new
            {
                broadcaster = AlphaBroadcaster, moderator = User(Bob), banned_user = User(Eve),
                metadata = new { reason = "spam", created_at = At(3620), expires_at = At(4220) },
            }),
            // One redemption, updated twice (pending → accepted) → counted once.
            Wh("wh-reward-1", KickEventTypes.ChannelRewardRedemptionUpdated, 100, Reward("pending", 100)),
            Wh("wh-reward-2", KickEventTypes.ChannelRewardRedemptionUpdated, 110, Reward("accepted", 110)),
            Wh("wh-live", KickEventTypes.LivestreamStatusUpdated, 0, new { broadcaster = AlphaBroadcaster, is_live = true }));

        db.ReceivedRealtimeEvents.AddRange(
            // alpha — overlaps the webhook stream
            RtChat("alpha", "a1", Alice, 0, "hello everyone"),                              // same id → merged by PK
            RtChat("alpha", "a4-rt", Bob, 30, "great, thanks @carol are you here?"),         // other id, same content → merged
            Rt("alpha", "UserBannedEvent", "ban-alpha", 3620, new
            {
                user = new { id = Eve.Id, username = Eve.Name }, banned_by = new { id = Bob.Id, username = Bob.Name },
                permanent = false, expires_at = At(4220),
            }),                                                                              // webhook owns bans here → skipped
            Rt("alpha", "MessageDeletedEvent", "del-a7", 3615, new { id = "del-a7", message = new { id = "a7" } }), // realtime-only → kept
            // beta — realtime-only channel
            RtChat("beta", "b1", Frank, 200, "anyone here?"),
            RtChat("beta", "b2", Gina, 205, "me!", reply: ("b1", Frank)),
            RtChat("beta", "b3", Frank, 210, "@gina nice"),
            RtChat("beta", "b4", Bob, 215, "hello", badge: "moderator"),
            Rt("beta", "MessageDeletedEvent", "del-b3", 220, new { id = "del-b3", message = new { id = "b3" }, aiModerated = true }),
            Rt("beta", "UserBannedEvent", "ban-gina", 230, new
            {
                user = new { id = Gina.Id, username = Gina.Name }, banned_by = new { id = Bob.Id, username = Bob.Name },
                permanent = false, expires_at = At(830),
            }),
            Rt("beta", "GiftedSubscriptionsEvent", "gift-beta", 240, new { gifter_username = "Frank", gifted_usernames = new[] { "Gina" } }),
            Rt("beta", "SubscriptionEvent", "sub-beta", 250, new { username = "Gina", months = 1 }),
            Rt("beta", "PollUpdateEvent", "poll", 260, new { poll = new { title = "?" } }));

        await db.SaveChangesAsync();
    }

    public static async Task ProjectAllAsync(KickGatewayDbContext db)
    {
        var horizon = DateTime.MaxValue;
        while (await ChatProjector.ProjectWebhooksAsync(db, 4, horizon, default) > 0) { }
        while (await ChatProjector.ProjectRealtimeAsync(db, 4, horizon, default) > 0) { }
    }

    private static string At(int seconds) => T0.AddSeconds(seconds).ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static object User((long Id, string Name) u) => new
    {
        is_anonymous = false, user_id = u.Id, username = u.Name, is_verified = false, channel_slug = u.Name.ToLowerInvariant(),
    };

    private static object Reward(string status, int t) => new
    {
        id = "RDM1", user_input = "", status, redeemed_at = At(t),
        reward = new { id = "r1", title = "Hydrate", cost = 100 },
        redeemer = User(Alice), broadcaster = AlphaBroadcaster,
    };

    private static ReceivedWebhook Wh(string id, string type, int t, object body) => new()
    {
        MessageId = id,
        EventType = type,
        SubscriptionId = "sub",
        BroadcasterAccountId = AlphaAccountId,
        ReceivedAt = T0.AddSeconds(t).AddMilliseconds(500),
        PublishedAt = T0.AddSeconds(t).AddMilliseconds(500),
        RawBody = JsonSerializer.Serialize(body),
    };

    private static ReceivedWebhook WhChat(string whId, string msgId, (long Id, string Name) s, int t, string content,
        (string Msg, (long Id, string Name) User)? reply = null) =>
        Wh(whId, KickEventTypes.ChatMessageSent, t, new
        {
            message_id = msgId,
            replies_to = reply is { } r ? new { message_id = r.Msg, content = "…", sender = User(r.User) } : null,
            broadcaster = AlphaBroadcaster,
            sender = new
            {
                is_anonymous = false, user_id = s.Id, username = s.Name, is_verified = false, channel_slug = s.Name.ToLowerInvariant(),
                identity = new { username_color = "#123456", badges = new[] { new { text = "Subscriber", type = "subscriber", count = 1 } } },
            },
            content,
            emotes = Array.Empty<object>(),
            created_at = At(t),
        });

    private static ReceivedRealtimeEvent Rt(string slug, string evt, string key, int t, object data) => new()
    {
        DedupeKey = $"{slug}|{evt}|{key}",
        EventName = $"App\\Events\\{evt}",
        PusherChannel = $"chatrooms.{slug}.v2",
        Slug = slug,
        ReceivedAt = T0.AddSeconds(t).AddMilliseconds(700),
        RawData = JsonSerializer.Serialize(data),
    };

    private static ReceivedRealtimeEvent RtChat(string slug, string id, (long Id, string Name) s, int t, string content,
        (string Msg, (long Id, string Name) User)? reply = null, string badge = "subscriber") =>
        Rt(slug, "ChatMessageEvent", id, t, new
        {
            id,
            chatroom_id = 5,
            content,
            type = reply is null ? "message" : "reply",
            created_at = At(t),
            sender = new
            {
                id = s.Id, username = s.Name, slug = s.Name.ToLowerInvariant(),
                identity = new { color = "#654321", badges = new[] { new { type = badge, text = badge, count = 0 } } },
            },
            metadata = reply is { } r
                ? new { original_sender = new { id = r.User.Id.ToString(), username = r.User.Name }, original_message = new { id = r.Msg, content = "…" } }
                : null,
        });
}

public class ChatAnalyticsIntegrationTests(ChatAnalyticsDatabase fixture) : IClassFixture<ChatAnalyticsDatabase>
{
    private static readonly AnalyticsWindow All = ChatSeed.All;
    private static readonly AnalyticsScope Everything = AnalyticsScope.Unrestricted;

    private ChatAnalyticsService Service(KickGatewayDbContext db) => new(db, Options.Create(new ChatAnalyticsOptions()));

    private void SkipWithoutDocker() => Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

    [SkippableFact]
    public async Task Projection_merges_duplicates_and_honours_webhook_authority()
    {
        SkipWithoutDocker();
        await using var db = fixture.Open();

        Assert.Equal(8, await db.ChatMessages.CountAsync(m => m.ChannelSlug == "alpha"));
        Assert.Equal(4, await db.ChatMessages.CountAsync(m => m.ChannelSlug == "beta"));
        Assert.False(await db.ChatMessages.AnyAsync(m => m.MessageId == "a4-rt"));
        Assert.Equal(ChatSource.Webhook, (await db.ChatMessages.SingleAsync(m => m.MessageId == "a1")).Source);

        var events = await db.ChatterEvents.AsNoTracking().ToListAsync();
        Assert.Equal(13, events.Count);
        // Realtime ban in the webhook channel was skipped; the webhook one is there.
        Assert.Single(events, e => e.ChannelSlug == "alpha" && e.Kind == ChatterEventKind.Timeout);
        Assert.Equal(ChatSource.Webhook, events.Single(e => e.ChannelSlug == "alpha" && e.Kind == ChatterEventKind.Timeout).Source);
        // Deleted message author resolved from the stored message.
        var deleted = events.Single(e => e.RefId == "a7");
        Assert.Equal(("5", "Eve"), (deleted.UserId, deleted.Username));
        // Username-only realtime rows resolved to ids via the chat seen in the batch.
        var gift = events.Single(e => e.ChannelSlug == "beta" && e.Kind == ChatterEventKind.SubscriptionGift);
        Assert.Equal(("6", "7"), (gift.UserId, gift.CounterpartUserId));
        Assert.Equal("7", events.Single(e => e.ChannelSlug == "beta" && e.Kind == ChatterEventKind.SubscriptionNew).UserId);

        var mentions = await db.ChatMentions.AsNoTracking().Select(x => x.MessageId + ":" + x.MentionedUsername).ToListAsync();
        Assert.Equal(new[] { "a4:carol", "a5:bob", "b3:gina" }, mentions.Order());

        var status = await Service(db).StatusAsync(Everything, default);
        Assert.All(status.Projections, p => Assert.Equal(0, p.PendingInboxRows));
        Assert.Equal(12, status.ChatMessages);
        Assert.Equal(13, status.ChatterEvents);
    }

    [SkippableFact]
    public async Task Overview_and_chatters()
    {
        SkipWithoutDocker();
        await using var db = fixture.Open();
        var svc = Service(db);

        var o = await svc.OverviewAsync(Everything, "alpha", All, default);
        Assert.Equal(8, o.Messages);
        Assert.Equal(5, o.Chatters);
        Assert.Equal(0.5, o.ReplyShare);
        Assert.Equal(0.25, o.MentionShare);
        Assert.Equal(6, o.MessagesByHourUtc[18]); // a1–a5 + a8 (next day, same hour)
        Assert.Equal(2, o.MessagesByHourUtc[19]);
        Assert.Equal(2, o.Daily.Count);
        Assert.Equal("Alice", o.TopChatters[0].Username);
        Assert.Equal(new ChannelEventTotals(Follows: 1, NewSubscriptions: 1, Renewals: 0, GiftedSubscriptions: 2, KicksGifted: 100,
            RewardRedemptions: 1, Bans: 0, Timeouts: 1, MessagesDeleted: 1), o.Events);

        // From T0+1h: dave + eve are new, alice (first seen at T0) is returning.
        var later = await svc.OverviewAsync(Everything, "alpha", new AnalyticsWindow(ChatSeed.T0.AddHours(1), All.To), default);
        Assert.Equal(3, later.Chatters);
        Assert.Equal(2, later.NewChatters);
        Assert.Equal(1, later.ReturningChatters);

        var page = await svc.ChattersAsync(Everything, "alpha", All, "messages", 10, 0, default);
        Assert.Equal(5, page.Total);
        var alice = page.Chatters[0];
        Assert.Equal(("1", "Alice", 3, 2, 2, 2), (alice.UserId, alice.Username, alice.Messages, alice.ActiveDays, alice.RepliesSent, alice.RepliesReceived));
        var carol = page.Chatters.Single(c => c.UserId == "3");
        Assert.Equal(1, carol.MentionsReceived);
        Assert.Equal(1, carol.RepliesReceived);

        var byLastSeen = await svc.ChattersAsync(Everything, "alpha", All, "last_seen", 2, 0, default);
        Assert.Equal("1", byLastSeen.Chatters[0].UserId);
        var second = await svc.ChattersAsync(Everything, "alpha", All, "messages", 2, 2, default);
        Assert.Equal(2, second.Chatters.Count);
        Assert.DoesNotContain(second.Chatters, c => c.UserId is "1" or "2");

        var channels = await svc.ChannelsAsync(Everything, All, default);
        Assert.Equal(new[] { ("alpha", 8, 5), ("beta", 4, 3) }, channels.Channels.Select(c => (c.Slug, c.Messages, c.Chatters)));
    }

    [SkippableFact]
    public async Task Interaction_graph()
    {
        SkipWithoutDocker();
        await using var db = fixture.Open();

        var g = await Service(db).GraphAsync(Everything, "alpha", All, new GraphOptions(), 50, 100, default);

        Assert.False(g.Truncated);
        var bobToAlice = Assert.Single(g.Edges, e => e.From == "2" && e.To == "1");
        Assert.Equal((2, 0, 1, 8.0), (bobToAlice.Replies, bobToAlice.Mentions, bobToAlice.Gifts, bobToAlice.Weight));
        Assert.Equal(1, Assert.Single(g.Edges, e => e.From == "1" && e.To == "2").Replies);
        Assert.Equal(1, Assert.Single(g.Edges, e => e.From == "2" && e.To == "3").Mentions);
        Assert.Equal(1.0, Assert.Single(g.Edges, e => e.From == "5" && e.To == "4").Proximity); // eve right after dave
        Assert.Equal("Alice", g.Summary.TopHubs[0]);
        Assert.Equal("Bob", g.Summary.TopInitiators[0]);

        var community = g.Nodes.ToDictionary(n => n.Key, n => n.Community);
        Assert.Equal(community["1"], community["2"]);
        Assert.Equal(community["1"], community["3"]);
        Assert.Equal(community["4"], community["5"]);
        Assert.NotEqual(community["1"], community["4"]);
        Assert.Equal(2, g.Communities.Count);
        Assert.Contains("Alice", g.Communities[0].TopMembers);

        var explicitOnly = await Service(db).GraphAsync(Everything, "alpha", All, new GraphOptions { IncludeProximity = false }, 50, 100, default);
        Assert.DoesNotContain(explicitOnly.Edges, e => e.Proximity > 0);
    }

    [SkippableFact]
    public async Task Chatter_profiles()
    {
        SkipWithoutDocker();
        await using var db = fixture.Open();
        var svc = Service(db);

        var alice = await svc.ProfileAsync(Everything, "@alice", null, All, default);
        Assert.NotNull(alice);
        Assert.Equal(("1", "Alice"), (alice!.UserId, alice.Username));
        Assert.Equal(3, alice.Activity.Messages);
        Assert.Equal(2, alice.Activity.ActiveDays);
        Assert.Equal(2, alice.Activity.Sessions);
        Assert.Equal(ChatSeed.T0, alice.Activity.FirstSeenAt);
        var alpha = Assert.Single(alice.Channels);
        Assert.Equal(("alpha", 1), (alpha.Slug, alpha.SubscriberMonths));
        var bob = alice.Social.TopPartners[0];
        Assert.Equal(("2", 1, 2, 1, 11.0), (bob.UserId, bob.RepliesTo, bob.RepliesFrom, bob.GiftsFrom, bob.Weight));
        Assert.Equal(2, alice.Social.RepliesSent);
        Assert.Equal(2, alice.Social.RepliesReceived);
        Assert.Single(alice.Support.Subscriptions);
        Assert.Equal(1, alice.Support.GiftedSubsReceived);
        Assert.Equal(new TermCount("Hydrate", 1), Assert.Single(alice.Support.Redemptions));
        Assert.Equal("a8", alice.RecentMessages[0].MessageId);

        var eve = await svc.ProfileAsync(Everything, "5", null, All, default);
        Assert.Equal(1, eve!.Moderation.TimeoutsReceived);
        Assert.Equal(1, eve.Moderation.MessagesDeleted);
        Assert.True(Assert.Single(eve.RecentMessages).Deleted);
        Assert.Equal(1.0, eve.Style.CapsShare);

        var bobProfile = await svc.ProfileAsync(Everything, "bob", null, All, default);
        Assert.Equal(2, bobProfile!.Moderation.ActionsIssued);
        Assert.Equal(new[] { "alpha", "beta" }, bobProfile.Channels.Select(c => c.Slug).Order());
        Assert.Contains("moderator", bobProfile.Channels.Single(c => c.Slug == "beta").Roles);

        var gina = await svc.ProfileAsync(Everything, "gina", null, All, default);
        Assert.Equal((1, 1, 1), (gina!.Support.GiftedSubsReceived, gina.Support.Subscriptions.Count, gina.Moderation.TimeoutsReceived));

        var frank = await svc.ProfileAsync(Everything, "frank", "beta", All, default);
        Assert.Equal(1, frank!.Moderation.MessagesDeleted);
        var ginaPartner = frank.Social.TopPartners.Single(p => p.UserId == "7");
        Assert.Equal((1, 1, 1), (ginaPartner.RepliesFrom, ginaPartner.MentionsTo, ginaPartner.GiftsTo));

        Assert.Null(await svc.ProfileAsync(Everything, "nobody", null, All, default));
    }

    [SkippableFact]
    public async Task Relationship_between_two_chatters()
    {
        SkipWithoutDocker();
        await using var db = fixture.Open();
        var svc = Service(db);

        var a = (await svc.ResolveUserAsync(Everything, "alice", default))!;
        var b = (await svc.ResolveUserAsync(Everything, "2", default))!;
        var r = await svc.RelationshipAsync(Everything, a, b, null, All, default);

        Assert.Equal((1, 0, 3.0, 10.0), (r!.AToB.Replies, r.AToB.GiftedSubs, r.AToB.Weight, r.AToB.MedianReplyLatencySeconds));
        Assert.Equal((2, 1, 8.0, 10.0), (r.BToA.Replies, r.BToA.GiftedSubs, r.BToA.Weight, r.BToA.MedianReplyLatencySeconds));
        Assert.Equal("mostly Bob → Alice", r.Balance);
        Assert.Equal(new[] { "alpha" }, r.SharedChannels);
        Assert.Equal(1, r.SharedActiveDays);
        Assert.Equal(new[] { "a4", "a3", "a2" }, r.Exchanges.Select(x => x.Message.MessageId));
        Assert.Equal("a2", r.Exchanges.Single(x => x.Message.MessageId == "a3").InReplyTo!.MessageId);

        var bobCarol = await svc.RelationshipAsync(Everything, b, (await svc.ResolveUserAsync(Everything, "carol", default))!, null, All, default);
        Assert.Equal((1, 1), (bobCarol!.AToB.Mentions, bobCarol.BToA.Mentions));
        Assert.Equal("mention", bobCarol.Exchanges[0].Kind);
    }

    [SkippableFact]
    public async Task Messages_search_paging_and_context()
    {
        SkipWithoutDocker();
        await using var db = fixture.Open();
        var svc = Service(db);

        var hits = await svc.MessagesAsync(Everything, "alpha", null, "bob", All, null, 50, default);
        Assert.Equal(new[] { "a5", "a3" }, hits.Messages.Select(m => m.MessageId));

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await svc.MessagesAsync(Everything, "alpha", null, null, All, cursor, 3, default);
            seen.AddRange(page.Messages.Select(m => m.MessageId));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(new[] { "a8", "a7", "a6", "a5", "a4", "a3", "a2", "a1" }, seen);

        var byUser = await svc.MessagesAsync(Everything, null, "6", null, All, null, 50, default);
        Assert.Equal(new[] { "b3", "b1" }, byUser.Messages.Select(m => m.MessageId));
        Assert.True(byUser.Messages[0].Deleted);

        var ctx = await svc.ContextAsync(Everything, "a3", 2, 2, default);
        Assert.Equal(new[] { "a1", "a2" }, ctx!.ReplyChain.Select(m => m.MessageId));
        Assert.Equal(new[] { "a4" }, ctx.Replies.Select(m => m.MessageId));
        Assert.Equal(new[] { "a1", "a2" }, ctx.Before.Select(m => m.MessageId));
        Assert.Equal(new[] { "a4", "a5" }, ctx.After.Select(m => m.MessageId));

        var found = await svc.SearchChattersAsync(Everything, "ali", null, 10, default);
        Assert.Equal("Alice", found[0].Username);
    }

    [SkippableFact]
    public async Task Scope_limits_everything_to_accessible_channels()
    {
        SkipWithoutDocker();
        await using var db = fixture.Open();
        var svc = Service(db);
        var betaOnly = AnalyticsScope.ForSlugs(new[] { "beta" });

        Assert.Null(await svc.ProfileAsync(betaOnly, "alice", null, All, default));
        var bob = await svc.ProfileAsync(betaOnly, "bob", null, All, default);
        Assert.Equal(new[] { "beta" }, bob!.Channels.Select(c => c.Slug));
        Assert.Equal(1, bob.Moderation.ActionsIssued);
        Assert.Equal(new[] { "beta" }, (await svc.ChannelsAsync(betaOnly, All, default)).Channels.Select(c => c.Slug));
        Assert.Empty((await svc.MessagesAsync(betaOnly, null, "2", "great", All, null, 50, default)).Messages);

        // Admin with a viewer role on alpha's client app → alpha only. SuperAdmin → everything.
        var viewer = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(AdminClaims.ClientRole, $"{ChatSeed.AlphaClientId}:{AdminRole.ClientViewer}"),
        }, "test"));
        var scope = await AnalyticsScope.ForUserAsync(viewer, db, default);
        Assert.Equal(new[] { "alpha" }, scope.Slugs);
        var super = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AdminClaims.GlobalRole, nameof(AdminRole.SuperAdmin)) }, "test"));
        Assert.Null((await AnalyticsScope.ForUserAsync(super, db, default)).Slugs);
        var nobody = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "test"));
        Assert.Empty((await AnalyticsScope.ForUserAsync(nobody, db, default)).Slugs!);
    }

    [SkippableFact]
    public async Task Replaying_the_inboxes_is_idempotent()
    {
        SkipWithoutDocker();
        var cs = await fixture.CreateDatabaseAsync("analytics_replay");
        await using (var db = ChatAnalyticsDatabase.Open(cs))
        {
            await ChatSeed.SeedAsync(db);
            await ChatSeed.ProjectAllAsync(db);
        }
        await using (var db = ChatAnalyticsDatabase.Open(cs))
        {
            Assert.Equal(0, await ChatProjector.ProjectWebhooksAsync(db, 100, DateTime.MaxValue, default));
            // Forget progress → full replay must not duplicate anything.
            db.AnalyticsCheckpoints.RemoveRange(db.AnalyticsCheckpoints);
            await db.SaveChangesAsync();
        }
        await using (var db = ChatAnalyticsDatabase.Open(cs))
        {
            await ChatSeed.ProjectAllAsync(db);
            Assert.Equal(12, await db.ChatMessages.CountAsync());
            Assert.Equal(13, await db.ChatterEvents.CountAsync());
            Assert.Equal(3, await db.ChatMentions.CountAsync());
        }
    }
}
