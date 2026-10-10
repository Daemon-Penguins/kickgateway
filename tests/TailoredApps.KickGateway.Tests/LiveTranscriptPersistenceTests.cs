using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Transcripts;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// LiveTranscript → LiveTranscriptConsumer → LiveTranscripts table → TranscriptQueries, against a real
/// SQL Server (testcontainer, real migrations). Skipped without Docker.
/// </summary>
public class LiveTranscriptPersistenceTests(ChatAnalyticsDatabase fixture) : IClassFixture<ChatAnalyticsDatabase>
{
    private static readonly DateTime T0 = new(2026, 10, 8, 22, 45, 20, DateTimeKind.Utc);

    private void SkipWithoutDocker() => Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

    private static LiveTranscript Transcript(double audioStart, double seconds, string text) => new()
    {
        BroadcasterSlug = "Alpha", // mixed case on purpose — stored lowercase
        KickChannelId = "7847847",
        StartedAt = T0.AddSeconds(audioStart),
        EndedAt = T0.AddSeconds(audioStart + seconds),
        AudioStartSeconds = audioStart,
        AudioSeconds = seconds,
        Text = text,
        Language = "pl",
        DetectedLanguage = "PL", // stored lowercase
        LanguageProbability = 0.97f,
        Confidence = 0.85f,
        Segments =
        [
            new LiveTranscriptSegment { StartedAt = T0.AddSeconds(audioStart), EndedAt = T0.AddSeconds(audioStart + 2.2), Text = text, Confidence = 0.94f },
        ],
        FirstMediaSequence = 6581,
        LastMediaSequence = 6587,
        Model = "LargeV3Turbo/Q5_0",
        TranscribedAt = T0.AddSeconds(audioStart + seconds + 2),
        ProcessingSeconds = 0.55,
    };

    [SkippableFact]
    public async Task Consumer_stores_each_slice_once_and_queries_page_by_time()
    {
        SkipWithoutDocker();
        var cs = await fixture.CreateDatabaseAsync("transcripts");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KickGatewayDbContext>(o => o.UseSqlServer(cs));
        services.AddMassTransitTestHarness(x => x.AddConsumer<LiveTranscriptConsumer>());
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var first = Transcript(0, 12.25, "Będzie pani zadowolona. A ty już zrobiłeś tygodniówkę?");
            var second = Transcript(12.25, 10.05, "Nie, nie, nie. No właśnie ostatnią wykopywałem.");
            var third = Transcript(22.3, 5.25, "Ich sage dir, ändere deinen Nick.") with { Language = "de", DetectedLanguage = "de", LanguageProbability = 0.88f };

            await harness.Bus.Publish(first);
            await harness.Bus.Publish(first);  // broker redelivery / duplicate → must not create a second row
            await harness.Bus.Publish(second);
            await harness.Bus.Publish(third);

            var consumed = harness.GetConsumerHarness<LiveTranscriptConsumer>().Consumed;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (await consumed.SelectAsync<LiveTranscript>().CountAsync() < 4 && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Assert.Equal(4, await consumed.SelectAsync<LiveTranscript>().CountAsync());

            await using var db = ChatAnalyticsDatabase.Open(cs);
            var rows = await db.LiveTranscripts.AsNoTracking().OrderBy(x => x.StartedAt).ToListAsync();

            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal("alpha", r.ChannelSlug));
            Assert.Equal(first.Text, rows[0].Text);
            Assert.Equal(1, rows[0].SegmentCount);
            Assert.Equal(6581, rows[0].FirstMediaSequence);
            Assert.Equal("LargeV3Turbo/Q5_0", rows[0].Model);
            Assert.Equal(LiveTranscriptRecord.BuildDedupeKey("alpha", first.StartedAt, 0), rows[0].DedupeKey);

            // Window covering only the second slice (overlap semantics: StartedAt < To && EndedAt >= From).
            var page = await TranscriptQueries.PageAsync(db, "alpha",
                new AnalyticsWindow(T0.AddSeconds(13), T0.AddSeconds(20)), null, null, null, 100, default);
            Assert.Single(page.Items);
            Assert.Equal(second.Text, page.Items[0].Text);
            Assert.Null(page.NextCursor);
            Assert.Equal(T0.AddSeconds(12.25), page.Items[0].Segments[0].StartedAt);

            // Keyset paging, oldest first.
            var p1 = await TranscriptQueries.PageAsync(db, "alpha", new AnalyticsWindow(null, T0.AddDays(1)), null, null, null, 2, default);
            Assert.Equal(2, p1.Items.Count);
            Assert.NotNull(p1.NextCursor);
            var p2 = await TranscriptQueries.PageAsync(db, "alpha", new AnalyticsWindow(null, T0.AddDays(1)), null, null, p1.NextCursor, 2, default);
            Assert.Single(p2.Items);
            Assert.Equal(third.Text, p2.Items[0].Text);
            Assert.Null(p2.NextCursor);

            // Text search is a substring match; LIKE wildcards in the query are literal.
            var search = await TranscriptQueries.PageAsync(db, "alpha", new AnalyticsWindow(null, T0.AddDays(1)), "wykopywałem", null, null, 10, default);
            Assert.Single(search.Items);
            var noWildcard = await TranscriptQueries.PageAsync(db, "alpha", new AnalyticsWindow(null, T0.AddDays(1)), "%", null, null, 10, default);
            Assert.Empty(noWildcard.Items);

            // Language filter (case-insensitive code) + the detection fields round-trip through the row and the DTO.
            var german = await TranscriptQueries.PageAsync(db, "alpha", new AnalyticsWindow(null, T0.AddDays(1)), null, "DE", null, 10, default);
            Assert.Single(german.Items);
            Assert.Equal(third.Text, german.Items[0].Text);
            Assert.Equal("de", german.Language);
            Assert.Equal("de", german.Items[0].DetectedLanguage);
            Assert.Equal(0.88f, german.Items[0].LanguageProbability);
            Assert.Equal(2, (await TranscriptQueries.PageAsync(db, "alpha", new AnalyticsWindow(null, T0.AddDays(1)), null, "pl", null, 10, default)).Items.Count);
            Assert.Equal("pl", rows[0].DetectedLanguage);
            Assert.Equal(0.97f, rows[0].LanguageProbability);

            // "What was being said when this chat message was sent?" — a moment inside the first slice.
            var around = await TranscriptQueries.AroundAsync(db, "alpha", T0.AddSeconds(5), 0, default);
            Assert.Single(around);
            Assert.Equal(first.Text, around[0].Text);

            // Other channels are invisible.
            Assert.Empty((await TranscriptQueries.PageAsync(db, "beta", new AnalyticsWindow(null, T0.AddDays(1)), null, null, null, 10, default)).Items);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public void Dedupe_key_is_culture_invariant_and_distinguishes_slices()
    {
        var a = LiveTranscriptRecord.BuildDedupeKey("alpha", T0, 12.25);
        var b = LiveTranscriptRecord.BuildDedupeKey("alpha", T0, 12.251);
        var c = LiveTranscriptRecord.BuildDedupeKey("alpha", T0.AddTicks(1), 12.25);

        Assert.Equal($"alpha|{T0.Ticks}|12.250", a);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Mapping_lowercases_slug_truncates_text_and_keeps_segments()
    {
        var m = Transcript(0, 12, new string('x', 5000));
        var r = LiveTranscriptConsumer.Map(m, "alpha");

        Assert.Equal(4000, r.Text.Length);
        Assert.Equal(1, r.SegmentCount);
        var segs = TranscriptJson.ReadSegments(r.SegmentsJson);
        Assert.Single(segs);
        Assert.Equal(0.94f, segs[0].Confidence);
        Assert.Equal(T0, segs[0].StartedAt);
    }

    [Fact]
    public void Mapping_keeps_the_language_decision_and_normalizes_codes()
    {
        var r = LiveTranscriptConsumer.Map(Transcript(0, 12, "hallo"), "alpha");
        Assert.Equal("pl", r.Language);
        Assert.Equal("pl", r.DetectedLanguage);
        Assert.Equal(0.97f, r.LanguageProbability);

        // Unsure reading: transcribed in the channel's language, the detector's different guess is kept alongside.
        var unsure = LiveTranscriptConsumer.Map(Transcript(0, 12, "hallo") with { Language = "pl", DetectedLanguage = "de", LanguageProbability = 0.41f }, "alpha");
        Assert.Equal("pl", unsure.Language);
        Assert.Equal("de", unsure.DetectedLanguage);
        Assert.Equal(0.41f, unsure.LanguageProbability);

        // Fixed-language transcriber (older or detection off): nothing detected.
        var fixedLang = LiveTranscriptConsumer.Map(Transcript(0, 12, "hej") with { DetectedLanguage = null, LanguageProbability = null }, "alpha");
        Assert.Null(fixedLang.DetectedLanguage);
        Assert.Null(fixedLang.LanguageProbability);

        var dto = TranscriptQueries.ToDto(unsure);
        Assert.Equal("de", dto.DetectedLanguage);
        Assert.Equal(0.41f, dto.LanguageProbability);
    }
}
