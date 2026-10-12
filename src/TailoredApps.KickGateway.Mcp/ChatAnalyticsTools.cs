using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TailoredApps.KickGateway.Mcp;

/// <summary>
/// MCP tools over the gateway's chat analytics API. One tool per endpoint; each returns the
/// endpoint's JSON unchanged.
/// </summary>
[McpServerToolType]
public sealed class ChatAnalyticsTools(GatewayClient gateway)
{
    private const string FromDoc = "Start of the time window (UTC): ISO-8601 like 2026-09-01 or 2026-09-01T18:00:00Z, a look-back like 30m/12h/7d/4w, or 'all'.";
    private const string ToDoc = "End of the time window (UTC), same formats as 'from'. Default: now.";
    private const string ChannelDoc = "Kick channel slug (lowercase, as in kick.com/<slug>).";
    private const string UserDoc = "Kick user id (digits) or username (current or former, with or without @).";

    [McpServerTool(Name = "analytics_status", Title = "Analytics status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("How fresh and how big the chat dataset is: projection lag per source (webhooks, realtime), message/event counts, " +
                 "oldest/newest message, the channels you may query, and 'transcripts' = speech-to-text coverage per channel (slices, " +
                 "first/last slice, pipeline delay, last 24 h with speech seconds and languages). Call first when results look empty " +
                 "or stale, and before concluding a streamer 'never said' something - no coverage means no speech data, not silence.")]
    public Task<string> Status(CancellationToken ct) =>
        gateway.GetAsync("/api/analytics/status", null, ct);

    [McpServerTool(Name = "list_channels", Title = "List channels", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Channels with chat in the window, busiest first: message count, distinct chatters, first/last message. Default window: last 30 days.")]
    public Task<string> ListChannels(
        [Description(FromDoc)] string? from = null,
        [Description(ToDoc)] string? to = null,
        CancellationToken ct = default) =>
        gateway.GetAsync("/api/analytics/channels", Q(("from", from), ("to", to)), ct);

    [McpServerTool(Name = "channel_overview", Title = "Channel overview", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Community snapshot of one channel: messages, chatters, new vs returning chatters, share of replies/@mentions, " +
                 "messages per UTC hour and per day, top 15 chatters, follow/sub/gift/kicks/ban/timeout/deletion totals, and " +
                 "'transcripts' (slices, speech seconds, languages, average confidence) when the window has speech-to-text. Default window: last 7 days.")]
    public Task<string> ChannelOverview(
        [Description(ChannelDoc)] string channel,
        [Description(FromDoc)] string? from = null,
        [Description(ToDoc)] string? to = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/channels/{Seg(channel)}/overview", Q(("from", from), ("to", to)), ct);

    [McpServerTool(Name = "list_chatters", Title = "List chatters", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Paged chatter list for a channel with per-chatter messages, active days, replies sent/received, @mentions received, " +
                 "first/last message and whether they are new in the window. Default window: last 7 days.")]
    public Task<string> ListChatters(
        [Description(ChannelDoc)] string channel,
        [Description(FromDoc)] string? from = null,
        [Description(ToDoc)] string? to = null,
        [Description("Order: messages (default), active_days, replies, last_seen, first_seen.")] string? sort = null,
        [Description("Page size, 1-200 (default 50).")] int? limit = null,
        [Description("Rows to skip for paging (default 0).")] int? offset = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/channels/{Seg(channel)}/chatters",
            Q(("from", from), ("to", to), ("sort", sort), ("limit", limit), ("offset", offset)), ct);

    [McpServerTool(Name = "interaction_graph", Title = "Chatter interaction graph", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Who talks to whom in a channel. Directed weighted edges from Kick replies, @mentions, gifted subs and (in slow chat only) " +
                 "turn-taking proximity; per-chatter attention given/received, mutual ties, Louvain communities (cliques) with cohesion, " +
                 "and hubs / initiators / bridge 'connectors'. The response has a 'legend' explaining every metric. Default window: last 7 days.")]
    public Task<string> InteractionGraph(
        [Description(ChannelDoc)] string channel,
        [Description(FromDoc)] string? from = null,
        [Description(ToDoc)] string? to = null,
        [Description("Max chatters returned, strongest first (default 40).")] int? maxNodes = null,
        [Description("Max edges returned among those chatters, heaviest first (default 100).")] int? maxEdges = null,
        [Description("Include the implicit proximity signal (default true). Set false to see explicit interactions only.")] bool? includeProximity = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/channels/{Seg(channel)}/interactions",
            Q(("from", from), ("to", to), ("maxNodes", maxNodes ?? 40), ("maxEdges", maxEdges ?? 100), ("proximity", includeProximity)), ct);

    [McpServerTool(Name = "find_chatters", Title = "Find chatters", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Find chatters by (part of) username or exact user id. Exact and prefix matches first.")]
    public Task<string> FindChatters(
        [Description("Username fragment (min 2 chars) or user id.")] string query,
        [Description("Optional channel slug to search within.")] string? channel = null,
        [Description("Max results, 1-50 (default 20).")] int? limit = null,
        CancellationToken ct = default) =>
        gateway.GetAsync("/api/analytics/chatters", Q(("q", query), ("channel", channel), ("limit", limit)), ct);

    [McpServerTool(Name = "chatter_profile", Title = "Chatter profile", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Personal profile of one chatter: known usernames, activity (first/last seen, active days, sessions, UTC hour and weekday " +
                 "histograms — weekday index 0 = Monday), per-channel presence with badges/roles/sub months, writing style (length, emotes, " +
                 "commands, questions, caps, top words/emotes), social circle (top partners with replies/mentions/gifts each way), support " +
                 "(follows, subs, gifts, kicks, redemptions), moderation (bans/timeouts received, deleted messages, actions issued as a mod) " +
                 "and the 25 latest messages. Default window: all history.")]
    public Task<string> ChatterProfile(
        [Description(UserDoc)] string user,
        [Description("Optional channel slug to restrict the profile to.")] string? channel = null,
        [Description(FromDoc + " Default: all history.")] string? from = null,
        [Description(ToDoc)] string? to = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/chatters/{Seg(user)}", Q(("channel", channel), ("from", from), ("to", to)), ct);

    [McpServerTool(Name = "chatter_relationship", Title = "Relationship between two chatters", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Dynamics between two chatters: replies, @mentions and gifted subs in each direction with weights, balance " +
                 "(mutual vs one-sided), median reply latency, shared channels and active days, moderation between them, and the latest " +
                 "40 direct exchanges with the message each reply answered. Default window: all history.")]
    public Task<string> ChatterRelationship(
        [Description(UserDoc)] string user,
        [Description("The other chatter — " + UserDoc)] string otherUser,
        [Description("Optional channel slug to restrict to.")] string? channel = null,
        [Description(FromDoc + " Default: all history.")] string? from = null,
        [Description(ToDoc)] string? to = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/chatters/{Seg(user)}/relationships/{Seg(otherUser)}",
            Q(("channel", channel), ("from", from), ("to", to)), ct);

    [McpServerTool(Name = "search_messages", Title = "Search / read chat messages", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Chat messages newest first, filtered by channel and/or user and optional text (substring). Use it to read what someone " +
                 "actually said or to pull a transcript of a period. Deleted messages are flagged. Pass the returned nextCursor to get older " +
                 "messages. Default window: 7 days for a channel, all history when a user is given.")]
    public Task<string> SearchMessages(
        [Description("Channel slug (required unless 'user' is given).")] string? channel = null,
        [Description(UserDoc + " (required unless 'channel' is given).")] string? user = null,
        [Description("Text the message must contain.")] string? text = null,
        [Description(FromDoc)] string? from = null,
        [Description(ToDoc)] string? to = null,
        [Description("Page size, 1-200 (default 50).")] int? limit = null,
        [Description("nextCursor from a previous page.")] string? cursor = null,
        CancellationToken ct = default) =>
        gateway.GetAsync("/api/analytics/messages",
            Q(("channel", channel), ("user", user), ("q", text), ("from", from), ("to", to), ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = "message_context", Title = "Conversation around a message", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The conversation around one message: the reply chain it answers (oldest first), the replies it received, and the " +
                 "channel messages right before and after it.")]
    public Task<string> MessageContext(
        [Description("Kick chat message id (from any message listing).")] string messageId,
        [Description("Channel messages to include before it, 0-100 (default 15).")] int? before = null,
        [Description("Channel messages to include after it, 0-100 (default 15).")] int? after = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/messages/{Seg(messageId)}/context", Q(("before", before), ("after", after)), ct);

    // ---- live speech-to-text (what the STREAM said, as opposed to what chat wrote) ----

    private const string SpeechDoc =
        "Speech comes from the live speech-to-text of the stream's audio (streamer plus surroundings), in slices of ~10-30 s; " +
        "'language' is what it was transcribed in, 'confidence' 0..1, 'translations' (e.g. pl) when a translation exists. " +
        "Times are stream time (UTC). It is best-effort: a gap means nothing was transcribed, not silence.";

    [McpServerTool(Name = "search_transcripts", Title = "Search / read live transcripts (speech-to-text)", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What was SAID on the stream: transcript slices oldest first, optionally filtered by a text fragment and/or ISO-639-1 " +
                 "language. Use it to find when the streamer talked about something, then stream_timeline around that moment to see " +
                 "how chat reacted. " + SpeechDoc + " Default window: last 7 days. Page with nextCursor.")]
    public Task<string> SearchTranscripts(
        [Description(ChannelDoc)] string channel,
        [Description("Text the slice must contain (case-insensitive substring).")] string? text = null,
        [Description("Keep only slices transcribed in this ISO-639-1 language (e.g. pl, en, de).")] string? language = null,
        [Description(FromDoc)] string? from = null,
        [Description(ToDoc)] string? to = null,
        [Description("Page size, 1-500 (default 50).")] int? limit = null,
        [Description("nextCursor from a previous page.")] string? cursor = null,
        [Description("Include per-segment timings (default false - several times the payload).")] bool? includeSegments = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/channels/{Seg(channel)}/transcripts",
            Q(("q", text), ("language", language), ("from", from), ("to", to), ("limit", limit ?? 50), ("cursor", cursor),
              ("segments", includeSegments == true)), ct);

    [McpServerTool(Name = "transcript_at", Title = "What was being said at a moment", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The transcript slices overlapping one moment (+/- tolerance seconds), e.g. the createdAt of a chat message, to see " +
                 "what a reaction refers to. Viewers react 10-20 s AFTER a sentence ends, so for a chat message look 10-30 s before it " +
                 "(tolerance 30 covers that). " + SpeechDoc)]
    public Task<string> TranscriptAt(
        [Description(ChannelDoc)] string channel,
        [Description("The moment, ISO-8601 UTC (e.g. 2026-10-11T20:15:30Z).")] string at,
        [Description("Seconds either side of 'at', 0-300 (default 20).")] double? toleranceSeconds = null,
        [Description("Include per-segment timings (default false).")] bool? includeSegments = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/channels/{Seg(channel)}/transcripts/at",
            Q(("at", at), ("tolerance", toleranceSeconds), ("segments", includeSegments == true)), ct);

    [McpServerTool(Name = "speech_reactions", Title = "How chat reacted to one sentence", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What chat wrote right after ONE transcript slice (its 'id' from search_transcripts, transcript_at or a speech item of " +
                 "stream_timeline): the slice with its previous and next slices, the messages in [endedAt + lagMin, endedAt + lagMax) " +
                 "oldest first, and 'stats' - messages, distinct chatters, messages per minute against the minute BEFORE the slice " +
                 "started ('ratio' > 1 = chat sped up; null = that minute was silent) and who replied first. Default lag 5-45 s, " +
                 "which covers the 10-20 s viewer delay. The reverse direction (what was said before a message) is transcript_at. " + SpeechDoc)]
    public Task<string> SpeechReactions(
        [Description(ChannelDoc)] string channel,
        [Description("Transcript slice id (the 'id' of a speech item).")] long sliceId,
        [Description("Window start, seconds after the slice's endedAt, 0-600 (default 5).")] double? lagMinSeconds = null,
        [Description("Window end, seconds after the slice's endedAt, 0-600 (default 45).")] double? lagMaxSeconds = null,
        [Description("Max messages to return, 1-500 (default 100); 'stats' always count the whole window.")] int? limit = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/channels/{Seg(channel)}/transcripts/{sliceId}/reactions",
            Q(("lagMin", lagMinSeconds), ("lagMax", lagMaxSeconds), ("limit", limit ?? 100)), ct);

    [McpServerTool(Name = "stream_timeline", Title = "Channel timeline: speech + chat + events", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One chronology of a channel, oldest first: what the stream SAID (kind 'speech'), what HAPPENED (kind 'event': " +
                 "follow, subscription_new/renewal/gift, kicks_gift, reward_redemption, ban, timeout, message_deleted, unban) and what " +
                 "chat WROTE (kind 'chat'), merged by stream time. This is the tool for 'what did the streamer say and how did chat " +
                 "respond': reactions to a sentence land ~10-20 s after its endedAt, so read the chat that follows each speech item. " +
                 "Window: from/to, or 'at' with beforeSeconds/afterSeconds around a moment; default = the last hour (pass from=... " +
                 "for an earlier stream). " + SpeechDoc + " Page with nextCursor; the response's 'chronology' explains the timing.")]
    public Task<string> StreamTimeline(
        [Description(ChannelDoc)] string channel,
        [Description(FromDoc + " Default: one hour before 'to'.")] string? from = null,
        [Description(ToDoc)] string? to = null,
        [Description("Alternative to from/to: a moment (ISO-8601 UTC or a look-back like 15m) to read around.")] string? at = null,
        [Description("With 'at': seconds before it, 0-3600 (default 60).")] double? beforeSeconds = null,
        [Description("With 'at': seconds after it, 0-3600 (default 180).")] double? afterSeconds = null,
        [Description("Comma-separated subset of speech,chat,events (default all).")] string? include = null,
        [Description("Drop speech below this confidence, 0..1 (default 0).")] double? minConfidence = null,
        [Description("Page size, 1-500 (default 100).")] int? limit = null,
        [Description("nextCursor from a previous page.")] string? cursor = null,
        CancellationToken ct = default) =>
        gateway.GetAsync($"/api/analytics/channels/{Seg(channel)}/timeline",
            Q(("from", from), ("to", to), ("at", at), ("before", beforeSeconds), ("after", afterSeconds), ("include", include),
              ("minConfidence", minConfidence), ("limit", limit ?? 100), ("cursor", cursor)), ct);

    private static string Seg(string value) => Uri.EscapeDataString(value.Trim());

    private static Dictionary<string, object?> Q(params (string Name, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value);
}
