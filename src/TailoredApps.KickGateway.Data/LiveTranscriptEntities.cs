using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace TailoredApps.KickGateway.Api.Data;

/// <summary>
/// One slice (~10–30 s) of live speech-to-text, as published by <c>Subscribers.Transcriber</c>
/// (<c>Contracts.Realtime.Media.LiveTranscript</c>) and persisted by the Api's
/// <c>LiveTranscriptConsumer</c>. Unlike the chat read model this is <b>not</b> derived from the
/// inboxes — the transcriber is a separate best-effort pipeline — so a lost message is simply a gap.
/// <para>
/// Chronology with the chat tables: all timestamps are UTC on the gateway's clock. Compare
/// <see cref="StartedAt"/>/<see cref="EndedAt"/> with <see cref="ReceivedWebhook.ReceivedAt"/>,
/// <see cref="ReceivedRealtimeEvent.ReceivedAt"/> or <see cref="ChatMessageRecord.CreatedAt"/> on the same
/// <see cref="ChannelSlug"/>. Speech reaches HLS a few seconds after it is said and viewers watch with
/// their own player delay, so chat reactions to a sentence land ~10–20 s after its <see cref="EndedAt"/>.
/// </para>
/// </summary>
public class LiveTranscriptRecord
{
    public long Id { get; set; }

    /// <summary>Idempotency key for broker redeliveries: slug + start time + audio-clock position (see <see cref="BuildDedupeKey"/>).</summary>
    [MaxLength(200)]
    public string DedupeKey { get; set; } = "";

    /// <summary>Broadcaster channel slug (lowercase) — the routing key the message arrived with.</summary>
    [MaxLength(120)]
    public string ChannelSlug { get; set; } = "";

    [MaxLength(64)]
    public string KickChannelId { get; set; } = "";

    /// <summary>Estimated UTC stream time the audio started (± one HLS segment).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Estimated UTC stream time the audio ended.</summary>
    public DateTime EndedAt { get; set; }

    /// <summary>Position on the transcriber's per-channel audio clock (seconds since its capture session started).</summary>
    public double AudioStartSeconds { get; set; }

    public double AudioSeconds { get; set; }

    /// <summary>Whole slice, Whisper segments joined with a space. Truncated to 4000 chars.</summary>
    [MaxLength(4000)]
    public string Text { get; set; } = "";

    [MaxLength(16)]
    public string Language { get; set; } = "";

    /// <summary>Average token probability 0..1 (0 = unknown).</summary>
    public float Confidence { get; set; }

    /// <summary>The individual Whisper segments (<c>startedAt</c>, <c>endedAt</c>, <c>text</c>, <c>confidence</c>) as a JSON array.</summary>
    public string SegmentsJson { get; set; } = "[]";

    public int SegmentCount { get; set; }

    public long FirstMediaSequence { get; set; }

    public long LastMediaSequence { get; set; }

    [MaxLength(64)]
    public string Model { get; set; } = "";

    /// <summary>When the transcriber finished the slice.</summary>
    public DateTime TranscribedAt { get; set; }

    public double ProcessingSeconds { get; set; }

    /// <summary>When the Api stored the row.</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public static string BuildDedupeKey(string slug, DateTime startedAt, double audioStartSeconds) =>
        $"{slug}|{startedAt.Ticks}|{audioStartSeconds.ToString("F3", CultureInfo.InvariantCulture)}";
}
