using Microsoft.EntityFrameworkCore;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Retention;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class InboxRetentionCutoffTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    private static AnalyticsCheckpoint At(DateTime position) => new() { Name = "cp", Position = position };

    [Fact]
    public void Not_projected_inbox_is_cut_by_age_alone() =>
        Assert.Equal(Now.AddDays(-7), InboxRetention.Cutoff(Now, 7, projected: false, checkpoint: null));

    [Fact]
    public void Projected_inbox_without_a_checkpoint_is_left_alone() =>
        Assert.Null(InboxRetention.Cutoff(Now, 7, projected: true, checkpoint: null));

    [Fact]
    public void A_lagging_projector_holds_the_cutoff_back() =>
        Assert.Equal(Now.AddDays(-9), InboxRetention.Cutoff(Now, 7, projected: true, checkpoint: At(Now.AddDays(-9))));

    [Fact]
    public void A_caught_up_projector_does_not_extend_the_window() =>
        Assert.Equal(Now.AddDays(-7), InboxRetention.Cutoff(Now, 7, projected: true, checkpoint: At(Now.AddMinutes(-1))));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Days_below_one_count_as_one(int days) =>
        Assert.Equal(Now.AddDays(-1), InboxRetention.Cutoff(Now, days, projected: false, checkpoint: null));
}

/// <summary><see cref="InboxRetention.PurgeAsync"/> against a real SQL Server (testcontainer, real migrations). Skipped without Docker.</summary>
public class InboxRetentionIntegrationTests(ChatAnalyticsDatabase fixture) : IClassFixture<ChatAnalyticsDatabase>
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly InboxRetentionOptions Retention = new() { Days = 7, BatchSize = 100, PauseMilliseconds = 0 };

    [SkippableFact]
    public async Task Purges_old_rows_in_batches_but_never_ahead_of_the_projector()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var cs = await fixture.CreateDatabaseAsync("retention_projected");
        await SeedAsync(cs);
        await using (var db = ChatAnalyticsDatabase.Open(cs))
        {
            // Webhooks: projector caught up. Realtime: projector 9 days behind. Twitch: never projected yet.
            db.AnalyticsCheckpoints.AddRange(
                new AnalyticsCheckpoint { Name = ChatProjector.WebhookCheckpoint, Position = Now.AddMinutes(-1) },
                new AnalyticsCheckpoint { Name = ChatProjector.RealtimeCheckpoint, Position = Now.AddDays(-9) });
            await db.SaveChangesAsync();
        }

        var deleted = await InboxRetention.PurgeAsync(() => ChatAnalyticsDatabase.Open(cs), Retention,
            new ChatAnalyticsOptions.ProjectionOptions(), Now, default);

        Assert.Equal(250 + 1, deleted["webhooks"]);   // every row older than 7 days, over three batches
        Assert.Equal(1, deleted["realtime"]);         // only the 10-day-old row: the 8-day-old one is not projected yet
        Assert.Equal(0, deleted["twitch"]);
        await using var check = ChatAnalyticsDatabase.Open(cs);
        Assert.Equal(new[] { "wh-new" }, await check.ReceivedWebhooks.Select(x => x.MessageId).ToListAsync());
        Assert.Equal(new[] { "rt-8d", "rt-new" }, await check.ReceivedRealtimeEvents.OrderBy(x => x.ReceivedAt).Select(x => x.DedupeKey).ToListAsync());
        Assert.Equal(3, await check.ReceivedTwitchEvents.CountAsync());
    }

    [SkippableFact]
    public async Task Without_the_projector_rows_go_by_age_alone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var cs = await fixture.CreateDatabaseAsync("retention_unprojected");
        await SeedAsync(cs);

        var deleted = await InboxRetention.PurgeAsync(() => ChatAnalyticsDatabase.Open(cs), Retention,
            new ChatAnalyticsOptions.ProjectionOptions { Enabled = false }, Now, default);

        Assert.Equal((251L, 2L, 2L), (deleted["webhooks"], deleted["realtime"], deleted["twitch"]));
        await using var check = ChatAnalyticsDatabase.Open(cs);
        Assert.Equal(new[] { "tw-new" }, await check.ReceivedTwitchEvents.Select(x => x.DedupeKey).ToListAsync());
    }

    /// <summary>Webhooks: 250 rows 10 days old, one 8 days old, one fresh. Realtime and Twitch: 10 d, 8 d, fresh.</summary>
    private static async Task SeedAsync(string cs)
    {
        await using var db = ChatAnalyticsDatabase.Open(cs);
        for (var i = 0; i < 250; i++)
            db.ReceivedWebhooks.Add(new ReceivedWebhook { MessageId = $"wh-old-{i:D3}", EventType = "chat.message.sent", ReceivedAt = Now.AddDays(-10).AddSeconds(i), RawBody = "{}" });
        db.ReceivedWebhooks.Add(new ReceivedWebhook { MessageId = "wh-8d", EventType = "chat.message.sent", ReceivedAt = Now.AddDays(-8), RawBody = "{}" });
        db.ReceivedWebhooks.Add(new ReceivedWebhook { MessageId = "wh-new", EventType = "chat.message.sent", ReceivedAt = Now.AddHours(-1), RawBody = "{}" });

        foreach (var (key, age) in new[] { ("10d", -10.0), ("8d", -8.0), ("new", -0.04) })
        {
            db.ReceivedRealtimeEvents.Add(new ReceivedRealtimeEvent { DedupeKey = $"rt-{key}", EventName = "App\\Events\\ChatMessageEvent", Slug = "alpha", ReceivedAt = Now.AddDays(age), RawData = "{}" });
            db.ReceivedTwitchEvents.Add(new ReceivedTwitchEvent { DedupeKey = $"tw-{key}", EventType = "channel.chat.message", BroadcasterLogin = "alpha", ReceivedAt = Now.AddDays(age), RawData = "{}" });
        }
        await db.SaveChangesAsync();
    }
}
