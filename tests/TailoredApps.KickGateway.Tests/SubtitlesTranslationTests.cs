using System.Text.Json;
using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subtitles;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>Translations land on the transcript they belong to and reach viewers as a second event under the same id.</summary>
public class SubtitlesTranslationTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 16, 0, 0, DateTimeKind.Utc);

    private static string Part(string text, int i) { var p = text.Split(". "); return i < p.Length ? p[i] : ""; }

    private static LiveTranscript Transcript(double audioStart, string text, string language = "de") => new()
    {
        BroadcasterSlug = "chef_jan",
        StartedAt = T0.AddSeconds(audioStart),
        EndedAt = T0.AddSeconds(audioStart + 10),
        AudioStartSeconds = audioStart,
        AudioSeconds = 10,
        Text = text,
        Language = language,
        Confidence = 0.8f,
        Segments =
        [
            new LiveTranscriptSegment { StartedAt = T0.AddSeconds(audioStart), EndedAt = T0.AddSeconds(audioStart + 5), Text = Part(text, 0), Confidence = 0.8f },
            new LiveTranscriptSegment { StartedAt = T0.AddSeconds(audioStart + 5), EndedAt = T0.AddSeconds(audioStart + 10), Text = Part(text, 1), Confidence = 0.8f },
        ],
        TranscribedAt = T0.AddSeconds(audioStart + 11),
    };

    private static LiveTranscriptTranslation Translation(double audioStart, string text, string target = "pl") => new()
    {
        BroadcasterSlug = "Chef_Jan",
        StartedAt = T0.AddSeconds(audioStart),
        EndedAt = T0.AddSeconds(audioStart + 10),
        AudioStartSeconds = audioStart,
        SourceLanguage = "de",
        TargetLanguage = target,
        Text = text,
        Segments =
        [
            new LiveTranscriptSegment { StartedAt = T0.AddSeconds(audioStart), EndedAt = T0.AddSeconds(audioStart + 5), Text = Part(text, 0), Confidence = 0.8f },
            new LiveTranscriptSegment { StartedAt = T0.AddSeconds(audioStart + 5), EndedAt = T0.AddSeconds(audioStart + 10), Text = Part(text, 1), Confidence = 0.8f },
        ],
        Provider = "stub",
        TranslatedAt = T0.AddSeconds(audioStart + 12),
        ProcessingSeconds = 0.7,
    };

    [Fact]
    public void Translation_attaches_to_its_transcript_by_start_and_audio_position()
    {
        var feed = new TranscriptFeed(new SubtitlesOptions());
        var a = feed.Publish(Transcript(0, "Guten Abend. Wie geht's"))!;
        var b = feed.Publish(Transcript(10, "Alles klar. Bis dann"))!;

        var updated = feed.AttachTranslation(Translation(10, "Wszystko jasne. Na razie"));

        Assert.NotNull(updated);
        Assert.Equal(b.Id, updated.Id);
        Assert.Equal("Wszystko jasne. Na razie", updated.Translations!["pl"]);
        Assert.Equal(2, updated.TranslatedSegments!["pl"].Length);
        Assert.Equal("Na razie", updated.TranslatedSegments["pl"][1].Text);
        Assert.Equal(T0.AddSeconds(15), updated.TranslatedSegments["pl"][1].StartedAt); // source timings kept
        Assert.Equal("Alles klar. Bis dann", updated.Text);                               // original untouched

        var backlog = feed.Backlog("chef_jan");
        Assert.Null(backlog[0].Translations);                                              // a's untouched
        Assert.Equal("Wszystko jasne. Na razie", backlog[1].Translations!["pl"]);          // merged into the backlog for late viewers
        Assert.Null(a.Translations);
    }

    [Fact]
    public void Translation_for_an_unknown_or_expired_transcript_is_dropped()
    {
        var feed = new TranscriptFeed(new SubtitlesOptions { MaxBacklogItems = 1 });
        feed.Publish(Transcript(0, "Eins. Zwei"));
        feed.Publish(Transcript(10, "Drei. Vier")); // pushes the first out of the backlog

        Assert.Null(feed.AttachTranslation(Translation(0, "Jeden. Dwa")));   // gone
        Assert.Null(feed.AttachTranslation(Translation(99, "Nic. Nic")));    // never existed
        Assert.Null(feed.AttachTranslation(Translation(10, "  ")));           // nothing to show
        Assert.NotNull(feed.AttachTranslation(Translation(10, "Trzy. Cztery")));
    }

    [Fact]
    public async Task Live_subscriber_gets_the_transcript_then_a_translation_event_with_the_same_id()
    {
        var feed = new TranscriptFeed(new SubtitlesOptions());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = feed.SubscribeAsync("chef_jan", 0, cts.Token).GetAsyncEnumerator(cts.Token);

        feed.Publish(Transcript(0, "Guten Abend. Wie geht's"));
        Assert.True(await enumerator.MoveNextAsync());
        var first = enumerator.Current;
        Assert.Equal(FeedMessage.Transcript, first.Kind);
        Assert.Null(first.Event.Translations);

        feed.AttachTranslation(Translation(0, "Dobry wieczór. Jak leci"));
        Assert.True(await enumerator.MoveNextAsync());
        var second = enumerator.Current;
        Assert.Equal(FeedMessage.Translation, second.Kind);
        Assert.Equal(first.Event.Id, second.Event.Id);
        Assert.Equal("Dobry wieczór. Jak leci", second.Event.Translations!["pl"]);

        var frame = Sse.Format(second);
        Assert.StartsWith($"id: {first.Event.Id}\nevent: translation\ndata: {{", frame);
        using var json = JsonDocument.Parse(frame.Split('\n')[2]["data: ".Length..]);
        Assert.Equal("Dobry wieczór. Jak leci", json.RootElement.GetProperty("translations").GetProperty("pl").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("translatedSegments").GetProperty("pl").GetArrayLength());

        cts.Cancel();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public void A_second_language_or_a_repeat_replaces_only_its_own_entry()
    {
        var feed = new TranscriptFeed(new SubtitlesOptions());
        feed.Publish(Transcript(0, "Guten Abend. Wie geht's"));
        feed.AttachTranslation(Translation(0, "Dobry wieczór. Jak leci"));
        feed.AttachTranslation(Translation(0, "Good evening. How are you", target: "en"));
        var updated = feed.AttachTranslation(Translation(0, "Dobry wieczór. Jak tam"))!;

        Assert.Equal("Dobry wieczór. Jak tam", updated.Translations!["pl"]);
        Assert.Equal("Good evening. How are you", updated.Translations["en"]);
    }
}
