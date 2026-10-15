using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subscribers.Transcriber;
using TailoredApps.KickGateway.Subscribers.Transcriber.Consumers;
using TailoredApps.KickGateway.Subscribers.Transcriber.Sinks;
using TailoredApps.TwitchGateway.Contracts.Media;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class TwitchTranscriberMappingTests
{
    [Fact]
    public void Twitch_segment_becomes_a_session_input_keyed_by_login_with_the_pdt_clock()
    {
        var pdt = new DateTime(2026, 10, 15, 15, 9, 26, 131, DateTimeKind.Utc);
        var seg = new TwitchAudioSegment
        {
            BroadcasterLogin = "Forsen",
            BroadcasterUserId = "22484632",
            StreamId = "41375541868",
            MediaSequence = 5644,
            DiscontinuitySequence = 2,
            Duration = 2.0,
            ProgramDateTime = pdt,
            CapturedAt = pdt.AddSeconds(9), // fetched late - must not move the clock
            SegmentUri = "https://cdn/seg5644.ts",
            Data = [0x47, 0x40, 0x00, 0x1c],
        };

        var s = TwitchAudioSegmentConsumer.ToSession(seg);

        Assert.Equal("forsen", s.BroadcasterSlug);
        Assert.Equal("22484632", s.KickChannelId);
        Assert.Equal(5644, s.MediaSequence);
        Assert.Equal(2, s.DiscontinuitySequence);
        Assert.Equal(pdt.AddSeconds(2), s.CapturedAt); // segment END, like the Kick capture's CapturedAt
        Assert.False(s.IsInitSegment);
        Assert.Equal("video/mp2t", s.ContentType);
        Assert.Equal(MediaContainer.TransportStream, s.DetectContainer());
    }

    [Fact]
    public void Without_program_date_time_the_capture_time_is_the_clock()
    {
        var captured = new DateTime(2026, 10, 15, 15, 10, 0, DateTimeKind.Utc);
        var s = TwitchAudioSegmentConsumer.ToSession(new TwitchAudioSegment { BroadcasterLogin = "x", Duration = 2, CapturedAt = captured, Data = [] });
        Assert.Equal(captured, s.CapturedAt);
    }

    [Fact]
    public void Transcript_converts_to_the_twitch_contract_field_for_field()
    {
        var t = new LiveTranscript
        {
            BroadcasterSlug = "forsen",
            KickChannelId = "22484632",
            StartedAt = new DateTime(2026, 10, 15, 15, 9, 26, DateTimeKind.Utc),
            EndedAt = new DateTime(2026, 10, 15, 15, 9, 40, DateTimeKind.Utc),
            AudioStartSeconds = 120.5,
            AudioSeconds = 14,
            Text = "hello chat",
            Language = "en",
            DetectedLanguage = "en",
            LanguageProbability = 0.97f,
            Confidence = 0.81f,
            Segments = [new LiveTranscriptSegment { StartedAt = new DateTime(2026, 10, 15, 15, 9, 26, DateTimeKind.Utc), EndedAt = new DateTime(2026, 10, 15, 15, 9, 30, DateTimeKind.Utc), Text = "hello", Confidence = 0.8f }],
            FirstMediaSequence = 5644,
            LastMediaSequence = 5650,
            Model = "LargeV3Turbo/Q5_0",
            TranscribedAt = new DateTime(2026, 10, 15, 15, 9, 45, DateTimeKind.Utc),
            ProcessingSeconds = 3.2,
        };

        var tw = BusTranscriptSink.ToTwitch(t);

        Assert.Equal("forsen", tw.BroadcasterLogin);
        Assert.Equal("22484632", tw.BroadcasterUserId);
        Assert.Equal(t.StartedAt, tw.StartedAt);
        Assert.Equal(t.EndedAt, tw.EndedAt);
        Assert.Equal(120.5, tw.AudioStartSeconds);
        Assert.Equal("hello chat", tw.Text);
        Assert.Equal("en", tw.Language);
        Assert.Equal(0.97f, tw.LanguageProbability);
        Assert.Single(tw.Segments);
        Assert.Equal("hello", tw.Segments[0].Text);
        Assert.Equal(5650, tw.LastMediaSequence);
        Assert.Equal("LargeV3Turbo/Q5_0", tw.Model);
        Assert.Equal(3.2, tw.ProcessingSeconds);
    }

    [Fact]
    public void Source_selects_the_queue_name_and_is_validated()
    {
        var kick = new TranscriberOptions();
        kick.Validate();
        Assert.False(kick.IsTwitch);
        Assert.Equal("transcriber", kick.EffectiveQueueName);

        var twitch = new TranscriberOptions { Source = "Twitch" };
        twitch.Validate();
        Assert.True(twitch.IsTwitch);
        Assert.Equal("transcriber-twitch", twitch.EffectiveQueueName);

        var custom = new TranscriberOptions { Source = "twitch", QueueName = "mac-mini-twitch" };
        Assert.Equal("mac-mini-twitch", custom.EffectiveQueueName);

        var bad = new TranscriberOptions { Source = "youtube" };
        Assert.Throws<ArgumentException>(bad.Validate);
    }
}
