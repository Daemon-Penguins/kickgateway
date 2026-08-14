namespace TailoredApps.KickGateway.Contracts.Realtime.Media;

/// <summary>
/// One raw HLS media segment of a managed channel's <b>live</b> stream, forwarded through
/// the broker so subscribers can store/relay the video themselves. Best-effort and
/// <b>ephemeral</b>: published directly (NOT through the inbox/outbox), routed by
/// <see cref="BroadcasterSlug"/>, and given a short broker TTL so segments to absent
/// consumers expire instead of piling up. Subscribers reassemble in <see cref="MediaSequence"/>
/// order (init segment first, when <see cref="IsInitSegment"/>) and remux if desired — the
/// bytes are passed through untouched (no transcoding on the gateway side).
/// </summary>
public record LiveVideoSegment
{
    /// <summary>Channel slug (lowercase) — the routing key.</summary>
    public string BroadcasterSlug { get; init; } = "";

    /// <summary>Kick numeric channel id.</summary>
    public string KickChannelId { get; init; } = "";

    /// <summary>HLS <c>EXT-X-MEDIA-SEQUENCE</c> of this segment — monotonically increasing within a variant.</summary>
    public long MediaSequence { get; init; }

    /// <summary>Running <c>EXT-X-DISCONTINUITY-SEQUENCE</c> (increments across ad/scene breaks).</summary>
    public int DiscontinuitySequence { get; init; }

    /// <summary>Segment duration in seconds (from <c>EXTINF</c>). 0 for the init segment.</summary>
    public double Duration { get; init; }

    /// <summary>True when this is the fMP4 initialization segment (<c>EXT-X-MAP</c>), not a media segment.</summary>
    public bool IsInitSegment { get; init; }

    /// <summary>Selected variant bandwidth (bps) from the master playlist.</summary>
    public long VariantBandwidth { get; init; }

    /// <summary>MIME type of <see cref="Data"/> (e.g. <c>video/mp2t</c> for TS, <c>video/mp4</c> for fMP4).</summary>
    public string ContentType { get; init; } = "video/mp2t";

    /// <summary>Absolute source URL the segment was fetched from (for provenance/debug).</summary>
    public string SegmentUri { get; init; } = "";

    /// <summary>When the listener fetched the segment (server clock, UTC).</summary>
    public DateTime CapturedAt { get; init; }

    /// <summary>The raw segment bytes.</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();
}
