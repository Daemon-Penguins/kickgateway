namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>Configuration section <c>Analytics</c>.</summary>
public sealed class ChatAnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>Minimum accepted <see cref="ApiKey"/> length — shorter keys are ignored (key access stays off).</summary>
    public const int MinApiKeyLength = 24;

    /// <summary>
    /// Shared secret for machine clients (the MCP server): sent as <c>Authorization: Bearer …</c>
    /// or <c>X-Api-Key</c>. Grants read access to <c>/api/analytics/*</c> only, across all
    /// channels. Empty (default) = key access disabled; the endpoints then accept only the admin
    /// cookie.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Default look-back for channel-level queries when <c>from</c> is omitted.</summary>
    public int DefaultWindowDays { get; set; } = 7;

    /// <summary>Upper bound on messages loaded to build an interaction graph (latest win).</summary>
    public int MaxGraphMessages { get; set; } = 200_000;

    /// <summary>Upper bound on a chatter's messages loaded for style/time statistics (latest win).</summary>
    public int MaxProfileMessages { get; set; } = 20_000;

    public ProjectionOptions Projection { get; set; } = new();

    public sealed class ProjectionOptions
    {
        /// <summary>Run the inbox → chat read-model projector in this process.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Also project <c>ReceivedRealtimeEvents</c> (Pusher) — adds realtime-only channels,
        /// message deletions and unbans. Webhooks alone when false.</summary>
        public bool IncludeRealtime { get; set; } = true;

        /// <summary>Inbox rows read per batch.</summary>
        public int BatchSize { get; set; } = 500;

        /// <summary>Idle delay once the projector has caught up.</summary>
        public int PollSeconds { get; set; } = 5;

        /// <summary>Only rows older than this are projected, so in-flight webhook transactions
        /// (whose ReceivedAt is stamped before commit) can't be skipped by the keyset cursor.</summary>
        public int LagSeconds { get; set; } = 30;
    }
}
