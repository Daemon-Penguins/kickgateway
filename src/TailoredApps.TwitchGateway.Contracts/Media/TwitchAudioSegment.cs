namespace TailoredApps.TwitchGateway.Contracts.Media;

/// <summary>
/// One raw HLS segment of a Twitch channel's <b>live</b> stream - by default the <c>audio_only</c>
/// rendition (AAC in MPEG-TS, ~160 kb/s), captured by <c>TailoredApps.TwitchGateway.Capture</c>
/// for the transcriber. Best-effort and <b>ephemeral</b>: published directly (no inbox/outbox),
/// routed by <see cref="BroadcasterLogin"/>, with a short broker TTL. Segments inside Twitch's
/// stitched ad breaks are never published (the capture skips them and bumps
/// <see cref="DiscontinuitySequence"/>), so a gap in <see cref="MediaSequence"/> is normal.
/// </summary>
public record TwitchAudioSegment
{
    /// <summary>Broadcaster's Twitch login (lowercase) - the routing key.</summary>
    public string BroadcasterLogin { get; init; } = "";

    public string BroadcasterUserId { get; init; } = "";

    /// <summary>Helix stream id of the broadcast (empty when the capture could not resolve it).</summary>
    public string StreamId { get; init; } = "";

    /// <summary>Rendition name from the master playlist, e.g. <c>audio_only</c>, <c>160p</c>.</summary>
    public string Rendition { get; init; } = "audio_only";

    /// <summary>HLS <c>EXT-X-MEDIA-SEQUENCE</c> of this segment.</summary>
    public long MediaSequence { get; init; }

    /// <summary>Running discontinuity counter (explicit <c>EXT-X-DISCONTINUITY</c> tags and skipped ad breaks).</summary>
    public int DiscontinuitySequence { get; init; }

    /// <summary>Segment duration in seconds (<c>EXTINF</c>).</summary>
    public double Duration { get; init; }

    /// <summary>MIME type of <see cref="Data"/> - Twitch serves MPEG-TS (<c>video/mp2t</c>) for every rendition.</summary>
    public string ContentType { get; init; } = "video/mp2t";

    /// <summary>Absolute source URL (provenance / debugging).</summary>
    public string SegmentUri { get; init; } = "";

    /// <summary>The playlist's <c>EXT-X-PROGRAM-DATE-TIME</c> for this segment (UTC) - Twitch stamps every segment, so stream time is exact.</summary>
    public DateTime? ProgramDateTime { get; init; }

    /// <summary>When the capture fetched the segment (server clock, UTC).</summary>
    public DateTime CapturedAt { get; init; }

    public byte[] Data { get; init; } = [];
}
