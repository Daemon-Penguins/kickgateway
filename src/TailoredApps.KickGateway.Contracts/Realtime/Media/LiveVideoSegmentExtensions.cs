namespace TailoredApps.KickGateway.Contracts.Realtime.Media;

/// <summary>Container format of a <see cref="LiveVideoSegment"/>'s bytes.</summary>
public enum MediaContainer
{
    Unknown = 0,

    /// <summary>MPEG-2 transport stream (<c>.ts</c>). Self-describing — segments can be decoded or concatenated as-is.</summary>
    TransportStream = 1,

    /// <summary>Fragmented MP4 / CMAF (<c>.m4s</c>). Needs the init segment (<c>EXT-X-MAP</c>) first.</summary>
    Fmp4 = 2,
}

/// <summary>Helpers for consumers of <see cref="LiveVideoSegment"/>.</summary>
public static class LiveVideoSegmentExtensions
{
    private const byte TsSyncByte = 0x47;
    private const int TsPacketSize = 188;

    /// <summary>
    /// Works out the container of a segment. The bytes are sniffed first (a TS sync byte every 188
    /// bytes, or an ISO-BMFF box header) because the CDN's metadata is unreliable: Kick's playback
    /// CDN serves TS as <c>application/octet-stream</c> and appends a query string to the URL, so
    /// neither <see cref="LiveVideoSegment.ContentType"/> nor a naive extension check can be trusted
    /// on its own. Falls back to the MIME type, then to the URL path's extension.
    /// </summary>
    public static MediaContainer DetectContainer(this LiveVideoSegment seg)
    {
        var sniffed = SniffContainer(seg.Data);
        if (sniffed != MediaContainer.Unknown) return sniffed;

        var ct = seg.ContentType ?? "";
        if (ct.Contains("mp2t", StringComparison.OrdinalIgnoreCase)) return MediaContainer.TransportStream;
        if (ct.Contains("mp4", StringComparison.OrdinalIgnoreCase) || ct.Contains("iso.segment", StringComparison.OrdinalIgnoreCase))
            return MediaContainer.Fmp4;

        var ext = UriPathExtension(seg.SegmentUri);
        return ext switch
        {
            ".ts" or ".m2ts" or ".mts" => MediaContainer.TransportStream,
            ".m4s" or ".mp4" or ".m4v" or ".m4a" or ".cmfv" or ".cmfa" => MediaContainer.Fmp4,
            _ => MediaContainer.Unknown,
        };
    }

    /// <summary>True when the segment is MPEG-TS (or cannot be identified as fMP4 — TS is the safe default for a self-describing container).</summary>
    public static bool IsTransportStream(this LiveVideoSegment seg) => seg.DetectContainer() != MediaContainer.Fmp4;

    /// <summary>Byte-level container detection; <see cref="MediaContainer.Unknown"/> when the data is too short or matches neither.</summary>
    public static MediaContainer SniffContainer(ReadOnlySpan<byte> data)
    {
        if (data.Length >= TsPacketSize * 2 + 1 &&
            data[0] == TsSyncByte && data[TsPacketSize] == TsSyncByte && data[TsPacketSize * 2] == TsSyncByte)
            return MediaContainer.TransportStream;

        if (data.Length >= 8)
        {
            // ISO-BMFF: 4-byte size, 4-byte box type. Media segments open with styp/moof/sidx/prft/emsg,
            // init segments with ftyp; "free"/"skip" padding boxes also occur.
            var type = data.Slice(4, 4);
            if (type.SequenceEqual("ftyp"u8) || type.SequenceEqual("styp"u8) || type.SequenceEqual("moof"u8) ||
                type.SequenceEqual("sidx"u8) || type.SequenceEqual("prft"u8) || type.SequenceEqual("emsg"u8) ||
                type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8) || type.SequenceEqual("moov"u8))
                return MediaContainer.Fmp4;
        }

        return MediaContainer.Unknown;
    }

    private static string UriPathExtension(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var abs) ? abs.AbsolutePath : uri;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];
        return Path.GetExtension(path).ToLowerInvariant();
    }
}
