namespace TailoredApps.KickGateway.Realtime;

/// <summary>
/// Configuration for the real-time listener, bound from the <c>Kick:Realtime</c> section.
/// </summary>
public class RealtimeOptions
{
    public const string SectionName = "Kick:Realtime";

    // === Pusher connection ===

    /// <summary>Pusher WebSocket host. Kick uses the us2 cluster.</summary>
    public string WsHost { get; set; } = "ws-us2.pusher.com";

    /// <summary>
    /// Kick's public Pusher app key. This ROTATES (last: 2026-10, the old <c>32cbd69e4b950bf97679</c>
    /// now gets <c>pusher:error 4001 "App key ... not in this cluster"</c> and the listener logs
    /// "refused by Pusher"). Fetch the current one with Kick's own negotiation, no login needed:
    /// <c>POST https://web.kick.com/api/v1/realtime/connection</c> with
    /// <c>{"client":{"id":"&lt;uuid&gt;","type":"web"},"capabilities":{"accepted_providers":[{"provider":"pusher"}]}}</c>
    /// returns <c>data.connections[0].credentials.{app_key,cluster}</c> (host = <c>ws-{cluster}.pusher.com</c>).
    /// Override via <c>Kick__Realtime__AppKey</c> (the VPS deploy maps <c>KICK_REALTIME_APP_KEY</c>).
    /// </summary>
    public string AppKey { get; set; } = "34bf7a0ff419a2a775b9";

    /// <summary>Pusher protocol version query param.</summary>
    public int ProtocolVersion { get; set; } = 7;

    /// <summary>Client version query param (cosmetic; sent to Pusher).</summary>
    public string ClientVersion { get; set; } = "8.4.0";

    // === Roster / subscriptions ===

    /// <summary>How often to re-read the managed-broadcaster roster and reconcile subscriptions.</summary>
    public int RosterRefreshSeconds { get; set; } = 30;

    /// <summary>Max Pusher channels per WebSocket connection before sharding onto another.</summary>
    public int MaxChannelsPerConnection { get; set; } = 90;

    /// <summary>Seconds between our keepalive pings (kept below Pusher's activity_timeout).</summary>
    public int PingIntervalSeconds { get; set; } = 100;

    public int ReconnectMinSeconds { get; set; } = 1;
    public int ReconnectMaxSeconds { get; set; } = 30;

    // === Ingest (frame → inbox + outbox) ===

    /// <summary>
    /// Ingest workers (= queue partitions). Frames are hashed by Pusher channel, so one worker keeps a
    /// chatroom's frames in order while other channels commit in parallel; each worker holds one DB
    /// connection while busy.
    /// </summary>
    public int IngestWorkers { get; set; } = 4;

    /// <summary>Frames buffered per worker before the newest is dropped (the socket is never stalled).</summary>
    public int IngestQueueCapacity { get; set; } = 1000;

    /// <summary>Extra attempts for a frame whose commit failed (transient SQL error) before it is dropped.</summary>
    public int IngestRetries { get; set; } = 2;

    /// <summary>Base delay between those attempts (grows linearly: 1×, 2×, …).</summary>
    public int IngestRetryDelayMs { get; set; } = 250;

    // === Live video capture ===

    public VideoOptions Video { get; set; } = new();

    public class VideoOptions
    {
        /// <summary>
        /// Global kill-switch for live-video capture. OFF by default so deploying the feature
        /// doesn't start pulling every managed channel's video at once. When on, capture runs
        /// continuously while live for channels with <c>VideoCaptureEnabled = true</c>.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Prefer the highest variant at or below this bitrate (kbps). 0 = highest available.</summary>
        public int MaxBitrateKbps { get; set; } = 1500;

        /// <summary>Drop (and warn on) any segment larger than this. Guards against oversized broker messages.</summary>
        public int MaxSegmentBytes { get; set; } = 6 * 1024 * 1024;

        /// <summary>Per-message broker TTL — segments to absent consumers expire instead of piling up.</summary>
        public int SegmentTtlSeconds { get; set; } = 45;

        /// <summary>
        /// How often to re-publish the fMP4 init segment (EXT-X-MAP) while capturing. The init is
        /// tiny but REQUIRED to build a valid file, so re-emitting it lets a subscriber that joins
        /// mid-stream (or a recorder that started after capture) bootstrap within this window
        /// instead of waiting forever. Bounds mid-join latency.
        /// </summary>
        public int InitRepublishSeconds { get; set; } = 10;

        /// <summary>How often to re-check live state to start/stop capture loops.</summary>
        public int LiveCheckSeconds { get; set; } = 30;
    }
}
