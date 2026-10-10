using TailoredApps.KickGateway.Contracts.Realtime.Media;
using TailoredApps.KickGateway.Subtitles;
using TailoredApps.KickGateway.Subtitles.Relay;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

/// <summary>
/// The delayed-video relay: a bounded per-channel buffer of the gateway's segments, renumbered into a
/// contiguous live playlist with program-date-time, a viewer cap, and a token that is the only door.
/// </summary>
public class VideoRelayTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 14, 0, 0, DateTimeKind.Utc);

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // 188-byte MPEG-TS sync pattern so DetectContainer sees a transport stream.
    private static byte[] TsBytes(int packets = 4)
    {
        var data = new byte[188 * packets];
        for (var i = 0; i < packets; i++) data[i * 188] = 0x47;
        return data;
    }

    private static LiveVideoSegment Seg(string slug, long seq, int disc = 0, double duration = 2, int packets = 4) => new()
    {
        BroadcasterSlug = slug,
        KickChannelId = "42",
        MediaSequence = seq,
        DiscontinuitySequence = disc,
        Duration = duration,
        VariantBandwidth = 1_400_000,
        ContentType = "application/octet-stream", // Kick's CDN lies; the bytes decide
        SegmentUri = $"https://cdn.example/{seq}.ts?dna=x",
        CapturedAt = T0.AddSeconds(seq * duration + duration),
        Data = TsBytes(packets),
    };

    private static (VideoRelay relay, FakeTime time, RelayOptions opts) Relay(int bufferSeconds = 90, int maxMb = 64, int maxViewers = 2, string? channels = null)
    {
        var maxDelay = Math.Max(0, bufferSeconds - 10);
        var opts = new RelayOptions
        {
            Token = "0123456789abcdef0123", BufferSeconds = bufferSeconds, MaxBufferMegabytesPerChannel = maxMb, MaxViewersPerChannel = maxViewers,
            ViewerIdleSeconds = 15, Channels = channels ?? "", MaxDelaySeconds = maxDelay, DefaultDelaySeconds = Math.Min(15, maxDelay),
        };
        opts.Validate();
        var time = new FakeTime(new DateTimeOffset(T0, TimeSpan.Zero));
        return (new VideoRelay(opts, new SubtitlesOptions(), time), time, opts);
    }

    // ---- options / token ----

    [Fact]
    public void Relay_is_off_without_a_long_enough_token()
    {
        Assert.False(new RelayOptions().Enabled);
        Assert.False(new RelayOptions { Token = "short" }.Enabled);
        Assert.Throws<ArgumentException>(() => new RelayOptions { Token = "short" }.Validate()); // configured but unusable → loud
        Assert.True(new RelayOptions { Token = "0123456789abcdef" }.Enabled);
    }

    [Fact]
    public void Token_comparison_is_exact_and_trimmed()
    {
        var o = new RelayOptions { Token = " 0123456789abcdef0123 " };
        Assert.True(o.TokenMatches("0123456789abcdef0123"));
        Assert.True(o.TokenMatches(" 0123456789abcdef0123\n"));
        Assert.False(o.TokenMatches("0123456789abcdef0124"));
        Assert.False(o.TokenMatches("0123456789abcdef012"));
        Assert.False(o.TokenMatches(null));
        Assert.False(new RelayOptions().TokenMatches("anything"));
    }

    [Fact]
    public void Delay_is_clamped_to_the_configured_range()
    {
        var o = new RelayOptions { DefaultDelaySeconds = 15, MaxDelaySeconds = 60 };
        Assert.Equal(15, o.ClampDelay(null));
        Assert.Equal(60, o.ClampDelay(999));
        Assert.Equal(0, o.ClampDelay(-5));
        Assert.Throws<ArgumentException>(() => new RelayOptions { BufferSeconds = 30, MaxDelaySeconds = 25 }.Validate());
    }

    // ---- buffering ----

    [Fact]
    public void Segments_are_renumbered_contiguously_and_stamped_with_content_start()
    {
        var (relay, _, _) = Relay();
        Assert.True(relay.Ingest(Seg("Alpha", 1000)));
        Assert.True(relay.Ingest(Seg("alpha", 1001)));

        var first = relay.Segment("alpha", 1)!;
        Assert.Equal(1000, first.MediaSequence);
        Assert.Equal(MediaContainer.TransportStream, first.Container);
        Assert.Equal("ts", first.Extension);
        Assert.Equal(T0.AddSeconds(1000 * 2), first.ContentStartUtc); // CapturedAt − Duration
        Assert.NotNull(relay.Segment("alpha", 2));
        Assert.Null(relay.Segment("alpha", 3));
        Assert.Equal(2, relay.Stats("alpha").Segments);
        Assert.Equal(4, relay.Stats("alpha").BufferedSeconds);
    }

    [Fact]
    public void Unusable_or_foreign_segments_are_dropped()
    {
        var (relay, _, _) = Relay(channels: "alpha");
        Assert.False(relay.Ingest(Seg("beta", 1)));           // not relayed
        Assert.False(relay.Ingest(Seg("bad slug", 1)));
        Assert.False(relay.Ingest(Seg("alpha", 1) with { Data = [] }));
        Assert.True(relay.Accepts("alpha"));
        Assert.False(relay.Accepts("beta"));
        Assert.Equal(0, relay.Stats("alpha").Segments);
    }

    [Fact]
    public void Buffer_is_trimmed_by_seconds_and_by_bytes()
    {
        var (bySeconds, _, _) = Relay(bufferSeconds: 20);
        for (var i = 1; i <= 15; i++) bySeconds.Ingest(Seg("alpha", i)); // 30 s of 2 s segments
        var stats = bySeconds.Stats("alpha");
        Assert.Equal(10, stats.Segments);
        Assert.Equal(20, stats.BufferedSeconds);
        Assert.Null(bySeconds.Segment("alpha", 5));   // oldest gone
        Assert.NotNull(bySeconds.Segment("alpha", 6));

        var (byBytes, _, _) = Relay(bufferSeconds: 600, maxMb: 4);
        for (var i = 1; i <= 40; i++) byBytes.Ingest(Seg("alpha", i, packets: 1000)); // 188 KB each → 40 ≈ 7.5 MB
        Assert.True(byBytes.Stats("alpha").BufferedBytes <= 4L * 1024 * 1024);
        Assert.True(byBytes.Stats("alpha").Segments is > 10 and < 40);
    }

    [Fact]
    public void A_hole_or_a_kick_discontinuity_becomes_a_playlist_discontinuity()
    {
        var (relay, _, _) = Relay();
        relay.Ingest(Seg("alpha", 1));
        relay.Ingest(Seg("alpha", 2));
        relay.Ingest(Seg("alpha", 4));           // 3 was dropped by the capture → hole
        relay.Ingest(Seg("alpha", 5));
        relay.Ingest(Seg("alpha", 6, disc: 1));  // Kick ad break
        relay.Ingest(Seg("alpha", 7, disc: 1));

        var playlist = relay.Playlist("alpha", "?token=t&cid=c")!;
        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Contains("#EXT-X-TARGETDURATION:2", lines);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:1", lines);
        Assert.Contains("#EXT-X-DISCONTINUITY-SEQUENCE:0", lines);
        Assert.Equal(2, lines.Count(l => l == "#EXT-X-DISCONTINUITY"));
        Assert.DoesNotContain(lines, l => l.StartsWith("#EXT-X-MAP")); // TS: no init
        Assert.Contains("seg/1.ts?token=t&cid=c", lines);
        Assert.Contains("seg/6.ts?token=t&cid=c", lines);               // contiguous local numbering despite the hole
        Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:2026-10-10T14:00:02.000Z", lines);
        Assert.Contains("#EXTINF:2.000,", lines);
        Assert.DoesNotContain("#EXT-X-ENDLIST", playlist);

        // The discontinuity sits right before the segment after the hole.
        var idx = Array.IndexOf(lines, "seg/3.ts?token=t&cid=c");
        Assert.Equal("#EXT-X-DISCONTINUITY", lines[idx - 3]);
    }

    [Fact]
    public void Playlist_window_moves_with_the_buffer_and_keeps_discontinuity_sequence()
    {
        var (relay, _, _) = Relay(bufferSeconds: 20);
        relay.Ingest(Seg("alpha", 1, duration: 5));
        relay.Ingest(Seg("alpha", 2, duration: 5));
        relay.Ingest(Seg("alpha", 9, duration: 5)); // hole → discontinuity index 1
        relay.Ingest(Seg("alpha", 10, duration: 5));
        relay.Ingest(Seg("alpha", 11, duration: 5));
        relay.Ingest(Seg("alpha", 12, duration: 5)); // 30 s buffered → trims 1 & 2 to get back to 20 s

        var lines = relay.Playlist("alpha", "")!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:3", lines);
        Assert.Contains("#EXT-X-DISCONTINUITY-SEQUENCE:1", lines); // the hole is before the window now
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", lines);
        Assert.Equal(4, lines.Count(l => l.StartsWith("seg/")));
    }

    [Fact]
    public void Init_segment_is_kept_and_mapped_for_fmp4()
    {
        var (relay, _, _) = Relay();
        var ftyp = new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        relay.Ingest(Seg("alpha", 0) with { IsInitSegment = true, Duration = 0, ContentType = "video/mp4", Data = ftyp });
        var moof = new byte[] { 0, 0, 0, 0x10, (byte)'m', (byte)'o', (byte)'o', (byte)'f', 0, 0, 0, 0, 0, 0, 0, 0 };
        relay.Ingest(Seg("alpha", 1) with { ContentType = "video/mp4", Data = moof });

        Assert.NotNull(relay.Init("alpha"));
        var playlist = relay.Playlist("alpha", "?token=t&cid=c")!;
        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4?token=t&cid=c\"", playlist);
        Assert.Contains("seg/1.m4s?token=t&cid=c", playlist);
        Assert.Equal("video/iso.segment", relay.Segment("alpha", 1)!.ContentType);
    }

    [Fact]
    public void Playlist_is_null_until_a_segment_arrives_and_cached_per_suffix()
    {
        var (relay, _, _) = Relay();
        Assert.Null(relay.Playlist("alpha", ""));
        relay.Ingest(Seg("alpha", 1));
        var a = relay.Playlist("alpha", "?token=a&cid=1");
        var b = relay.Playlist("alpha", "?token=a&cid=2");
        Assert.NotEqual(a, b);                     // suffix is part of the URIs
        Assert.Same(b, relay.Playlist("alpha", "?token=a&cid=2")); // cached until the next segment
        relay.Ingest(Seg("alpha", 2));
        Assert.NotSame(b, relay.Playlist("alpha", "?token=a&cid=2"));
    }

    // ---- viewers / transfer ----

    [Fact]
    public void Viewer_cap_frees_slots_when_a_viewer_goes_idle()
    {
        var (relay, time, _) = Relay(maxViewers: 2);
        Assert.True(relay.TouchViewer("alpha", "v1"));
        Assert.True(relay.TouchViewer("alpha", "v2"));
        Assert.False(relay.TouchViewer("alpha", "v3"));  // full
        Assert.True(relay.TouchViewer("alpha", "v1"));   // known viewer keeps its slot
        Assert.Equal(2, relay.Stats("alpha").Viewers);

        time.Now = time.Now.AddSeconds(16);              // v1 and v2 idle past ViewerIdleSeconds
        Assert.True(relay.TouchViewer("alpha", "v3"));
        Assert.Equal(1, relay.Stats("alpha").Viewers);
        Assert.True(relay.TouchViewer("beta", "v1"));    // caps are per channel
    }

    [Fact]
    public void Served_bytes_are_counted_per_channel()
    {
        var (relay, _, _) = Relay();
        relay.Ingest(Seg("alpha", 1));
        relay.CountServed("alpha", 1000);
        relay.CountServed("alpha", 2000);
        var s = relay.Stats("alpha");
        Assert.Equal(3000, s.ServedBytes);
        Assert.Equal(2, s.ServedSegments);
        Assert.Equal(["alpha"], relay.Snapshot().Select(x => x.Slug));
    }
}
