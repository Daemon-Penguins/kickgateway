using System.Security.Claims;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Api.Transcripts;

namespace TailoredApps.KickGateway.Api.Endpoints;

/// <summary>
/// Read-only chat analytics over the projected read model — chatter profiles and the
/// interaction dynamics between chatters. Designed as the backend of the MCP server
/// (<c>TailoredApps.KickGateway.Mcp</c>): accepts the admin cookie or the analytics API key,
/// and every response is limited to the channels the caller may see.
/// </summary>
public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/analytics")
            .RequireAuthorization(AnalyticsAuth.Policy)
            .AddEndpointFilter<AnalyticsRequestFilter>()   // READ UNCOMMITTED + JSON 503/500
            .WithTags("Chat analytics");

        // `platform` (kick | twitch) selects the platform's rows: optional on /status and /channels (null = all), and
        // defaulting to kick everywhere a channel or chatter is named - slugs and user ids are only unique per platform.
        g.MapGet("/status", async (string? platform, ClaimsPrincipal user, KickGatewayDbContext db, ChatAnalyticsService svc, CancellationToken ct) =>
        {
            if (!TryPlatform(platform, required: false, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            return Json(await svc.StatusAsync(scope, ct));
        });

        g.MapGet("/channels", async (string? from, string? to, string? platform,
            ClaimsPrincipal user, KickGatewayDbContext db, ChatAnalyticsService svc, CancellationToken ct) =>
        {
            if (!AnalyticsTime.TryResolveWindow(from, to, 30, DateTime.UtcNow, out var window, out var err)) return Bad(err);
            if (!TryPlatform(platform, required: false, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            return Json(await svc.ChannelsAsync(scope, window, ct));
        });

        g.MapGet("/channels/{slug}/overview", async (string slug, string? from, string? to, string? platform,
            ClaimsPrincipal user, KickGatewayDbContext db, ChatAnalyticsService svc, IOptions<ChatAnalyticsOptions> o, CancellationToken ct) =>
        {
            if (!AnalyticsTime.TryResolveWindow(from, to, o.Value.DefaultWindowDays, DateTime.UtcNow, out var window, out var err)) return Bad(err);
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            var channel = Slug(slug);
            if (!scope.Allows(channel)) return ChannelNotFound(channel);
            return Json(await svc.OverviewAsync(scope, channel, window, ct));
        });

        g.MapGet("/channels/{slug}/chatters", async (string slug, string? from, string? to, string? sort, int? limit, int? offset, string? platform,
            ClaimsPrincipal user, KickGatewayDbContext db, ChatAnalyticsService svc, IOptions<ChatAnalyticsOptions> o, CancellationToken ct) =>
        {
            if (!AnalyticsTime.TryResolveWindow(from, to, o.Value.DefaultWindowDays, DateTime.UtcNow, out var window, out var err)) return Bad(err);
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            var channel = Slug(slug);
            if (!scope.Allows(channel)) return ChannelNotFound(channel);
            return Json(await svc.ChattersAsync(scope, channel, window, (sort ?? "messages").Trim().ToLowerInvariant(),
                Math.Clamp(limit ?? 50, 1, 200), Math.Max(0, offset ?? 0), ct));
        });

        g.MapGet("/channels/{slug}/interactions", async (string slug, string? from, string? to, int? maxNodes, int? maxEdges,
            bool? proximity, double? replyWeight, double? mentionWeight, double? giftWeight, double? proximityWeight, string? platform,
            ClaimsPrincipal user, KickGatewayDbContext db, ChatAnalyticsService svc, IOptions<ChatAnalyticsOptions> o, CancellationToken ct) =>
        {
            if (!AnalyticsTime.TryResolveWindow(from, to, o.Value.DefaultWindowDays, DateTime.UtcNow, out var window, out var err)) return Bad(err);
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            var channel = Slug(slug);
            if (!scope.Allows(channel)) return ChannelNotFound(channel);

            var defaults = new GraphOptions();
            var weights = defaults with
            {
                IncludeProximity = proximity ?? defaults.IncludeProximity,
                ReplyWeight = NonNegative(replyWeight, defaults.ReplyWeight),
                MentionWeight = NonNegative(mentionWeight, defaults.MentionWeight),
                GiftWeight = NonNegative(giftWeight, defaults.GiftWeight),
                ProximityWeight = NonNegative(proximityWeight, defaults.ProximityWeight),
            };
            return Json(await svc.GraphAsync(scope, channel, window, weights, maxNodes ?? 60, maxEdges ?? 150, ct));
        });

        g.MapGet("/chatters", async (string? q, string? channel, int? limit, string? platform,
            ClaimsPrincipal user, KickGatewayDbContext db, ChatAnalyticsService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().TrimStart('@').Length < 2) return Bad("'q' needs at least 2 characters");
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            var ch = OptionalSlug(channel);
            if (ch is not null && !scope.Allows(ch)) return ChannelNotFound(ch);
            return Json(await svc.SearchChattersAsync(scope, q, ch, Math.Clamp(limit ?? 20, 1, 50), ct));
        });

        g.MapGet("/chatters/{user}", async (string user, string? channel, string? from, string? to, string? platform,
            ClaimsPrincipal principal, KickGatewayDbContext db, ChatAnalyticsService svc, CancellationToken ct) =>
        {
            // Profiles default to all history: they're indexed per user, and "who is this person"
            // shouldn't silently forget last month.
            if (!AnalyticsTime.TryResolveWindow(from, to, null, DateTime.UtcNow, out var window, out var err)) return Bad(err);
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(principal, db, ct)).WithPlatform(p);
            var ch = OptionalSlug(channel);
            if (ch is not null && !scope.Allows(ch)) return ChannelNotFound(ch);
            var profile = await svc.ProfileAsync(scope, user, ch, window, ct);
            return profile is null ? ChatterNotFound(user) : Json(profile);
        });

        g.MapGet("/chatters/{user}/relationships/{other}", async (string user, string other, string? channel, string? from, string? to, string? platform,
            ClaimsPrincipal principal, KickGatewayDbContext db, ChatAnalyticsService svc, CancellationToken ct) =>
        {
            if (!AnalyticsTime.TryResolveWindow(from, to, null, DateTime.UtcNow, out var window, out var err)) return Bad(err);
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(principal, db, ct)).WithPlatform(p);
            var ch = OptionalSlug(channel);
            if (ch is not null && !scope.Allows(ch)) return ChannelNotFound(ch);

            var a = await svc.ResolveUserAsync(scope, user, ct);
            if (a is null) return ChatterNotFound(user);
            var b = await svc.ResolveUserAsync(scope, other, ct);
            if (b is null) return ChatterNotFound(other);
            if (a.UserId == b.UserId) return Bad("both parameters resolve to the same chatter");
            return Json(await svc.RelationshipAsync(scope, a, b, ch, window, ct));
        });

        g.MapGet("/messages", async (string? channel, string? user, string? q, string? from, string? to, string? cursor, int? limit, string? platform,
            ClaimsPrincipal principal, KickGatewayDbContext db, ChatAnalyticsService svc, IOptions<ChatAnalyticsOptions> o, CancellationToken ct) =>
        {
            var ch = OptionalSlug(channel);
            if (ch is null && string.IsNullOrWhiteSpace(user)) return Bad("pass 'channel' and/or 'user'");
            // Transcript of one user → all history by default; channel-wide → the default window.
            int? defaultDays = string.IsNullOrWhiteSpace(user) ? o.Value.DefaultWindowDays : null;
            if (!AnalyticsTime.TryResolveWindow(from, to, defaultDays, DateTime.UtcNow, out var window, out var err)) return Bad(err);
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);

            var scope = (await AnalyticsScope.ForUserAsync(principal, db, ct)).WithPlatform(p);
            if (ch is not null && !scope.Allows(ch)) return ChannelNotFound(ch);
            string? userId = null;
            if (!string.IsNullOrWhiteSpace(user))
            {
                var who = await svc.ResolveUserAsync(scope, user, ct);
                if (who is null) return ChatterNotFound(user);
                userId = who.UserId;
            }
            return Json(await svc.MessagesAsync(scope, ch, userId, q, window, cursor, Math.Clamp(limit ?? 50, 1, 200), ct));
        });

        g.MapGet("/messages/{messageId}/context", async (string messageId, int? before, int? after, string? platform,
            ClaimsPrincipal principal, KickGatewayDbContext db, ChatAnalyticsService svc, CancellationToken ct) =>
        {
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(principal, db, ct)).WithPlatform(p);
            var context = await svc.ContextAsync(scope, messageId.Trim(), Math.Clamp(before ?? 15, 0, 100), Math.Clamp(after ?? 15, 0, 100), ct);
            return context is null ? Results.NotFound(new { error = $"message '{messageId}' not found" }) : Json(context);
        });

        // Live speech-to-text stored from Subscribers.Transcriber (see docs/CHAT-ANALYTICS.md).
        // segments=false drops the per-segment timings (the MCP server's default - a fraction of the payload).
        g.MapGet("/channels/{slug}/transcripts", async (string slug, string? from, string? to, string? q, string? language, string? cursor, int? limit, bool? segments, string? platform,
            ClaimsPrincipal user, KickGatewayDbContext db, IOptions<ChatAnalyticsOptions> o, CancellationToken ct) =>
        {
            if (!AnalyticsTime.TryResolveWindow(from, to, o.Value.DefaultWindowDays, DateTime.UtcNow, out var window, out var err)) return Bad(err);
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            var channel = Slug(slug);
            if (!scope.Allows(channel)) return ChannelNotFound(channel);
            return Json(await TranscriptQueries.PageAsync(db, p!, channel, window, q, language, cursor, Math.Clamp(limit ?? 100, 1, 500), ct, segments ?? true));
        });

        // What was being said at a given moment (e.g. the CreatedAt of a chat message), +/- tolerance seconds.
        g.MapGet("/channels/{slug}/transcripts/at", async (string slug, DateTime at, double? tolerance, bool? segments, string? platform,
            ClaimsPrincipal user, KickGatewayDbContext db, CancellationToken ct) =>
        {
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            var channel = Slug(slug);
            if (!scope.Allows(channel)) return ChannelNotFound(channel);
            var utc = at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime();
            return Json(await TranscriptQueries.AroundAsync(db, p!, channel, utc, Math.Clamp(tolerance ?? 20, 0, 300), ct, segments ?? true));
        });

        // What chat wrote right after one speech slice: the slice with its neighbours, the messages in
        // [endedAt + lagMin, endedAt + lagMax) and their rate against the minute before the slice. Bounded seeks only.
        g.MapGet("/channels/{slug}/transcripts/{id:long}/reactions", async (string slug, long id, double? lagMin, double? lagMax, int? limit, string? platform,
            ClaimsPrincipal user, KickGatewayDbContext db, CancellationToken ct) =>
        {
            var min = Math.Clamp(lagMin ?? SpeechReactionQueries.DefaultLagMin, 0, SpeechReactionQueries.MaxLag);
            var max = Math.Clamp(lagMax ?? SpeechReactionQueries.DefaultLagMax, 0, SpeechReactionQueries.MaxLag);
            if (max <= min) return Bad($"lagMax ({max}) must be greater than lagMin ({min})");
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);
            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            var channel = Slug(slug);
            if (!scope.Allows(channel)) return ChannelNotFound(channel);
            var reactions = await SpeechReactionQueries.ForSliceAsync(db, p!, channel, id, min, max,
                Math.Clamp(limit ?? SpeechReactionQueries.DefaultLimit, 1, SpeechReactionQueries.MaxLimit), ct);
            return reactions is null
                ? Results.NotFound(new { error = $"transcript slice {id} not found on channel '{channel}'" })
                : Json(reactions);
        });

        // One chronology of the channel: speech slices (LiveTranscripts), chatter events and chat messages merged by
        // stream time, oldest first, keyset-paged. Window = from/to, or 'at' with before/after seconds around a moment;
        // default = the last hour. Three bounded index seeks per page (see TimelineQueries) - never a join or a scan.
        g.MapGet("/channels/{slug}/timeline", async (string slug, string? from, string? to, string? at, double? before, double? after,
            string? include, float? minConfidence, string? cursor, int? limit, string? platform,
            ClaimsPrincipal user, KickGatewayDbContext db, CancellationToken ct) =>
        {
            if (!TimelineKinds.TryParseInclude(include, out var kinds, out var err)) return Bad(err);
            if (!TryPlatform(platform, required: true, out var p, out var perr)) return Bad(perr);

            var now = DateTime.UtcNow;
            AnalyticsWindow window;
            if (!string.IsNullOrWhiteSpace(at))
            {
                if (!AnalyticsTime.TryParse(at, now, out var moment) || moment is null)
                    return Bad($"invalid 'at': {at} (use ISO-8601 like 2026-09-01T18:00:00Z or a look-back like 15m)");
                window = new AnalyticsWindow(moment.Value.AddSeconds(-Math.Clamp(before ?? 60, 0, 3600)), moment.Value.AddSeconds(Math.Clamp(after ?? 180, 0, 3600)));
            }
            else
            {
                if (!AnalyticsTime.TryResolveWindow(from, to, null, now, out window, out err)) return Bad(err);
                if (window.From is null && !string.Equals(from?.Trim(), "all", StringComparison.OrdinalIgnoreCase))
                    window = window with { From = window.To - TimelineQueries.DefaultLookBack };
            }

            var scope = (await AnalyticsScope.ForUserAsync(user, db, ct)).WithPlatform(p);
            var channel = Slug(slug);
            if (!scope.Allows(channel)) return ChannelNotFound(channel);
            return Json(await TimelineQueries.PageAsync(db, p!, channel, window, kinds, Math.Clamp(minConfidence ?? 0, 0, 1), cursor,
                Math.Clamp(limit ?? TimelineQueries.DefaultLimit, 1, TimelineQueries.MaxLimit), ct));
        });

        return routes;
    }

    /// <summary>Validates <c>platform</c>: <c>kick</c> / <c>twitch</c>; empty = kick when <paramref name="required"/>, else null (all platforms).</summary>
    private static bool TryPlatform(string? raw, bool required, out string? platform, out string? error)
    {
        error = null;
        platform = string.IsNullOrWhiteSpace(raw) ? (required ? ChatPlatforms.Kick : null) : ChatPlatforms.Normalize(raw);
        if (platform is not null && !ChatPlatforms.IsKnown(platform))
        {
            error = $"unknown platform '{raw}' (use {string.Join(" or ", ChatPlatforms.All)})";
            return false;
        }
        return true;
    }

    private static IResult Json<T>(T value) => Results.Json(value, AnalyticsJson.Options);

    private static IResult Bad(string? error) => Results.BadRequest(new { error });

    private static IResult ChannelNotFound(string slug) =>
        Results.NotFound(new { error = $"channel '{slug}' not found or not accessible" });

    private static IResult ChatterNotFound(string user) =>
        Results.NotFound(new { error = $"no chatter '{user}' in accessible channels (try /api/analytics/chatters?q=…)" });

    private static string Slug(string slug) => slug.Trim().ToLowerInvariant();

    private static string? OptionalSlug(string? slug) => string.IsNullOrWhiteSpace(slug) ? null : Slug(slug);

    private static double NonNegative(double? value, double fallback) => value is >= 0 ? value.Value : fallback;
}
