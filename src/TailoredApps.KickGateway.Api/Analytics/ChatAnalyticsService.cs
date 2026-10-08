using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>
/// Read-only queries over the chat read model (ChatMessages / ChatMentions / ChatterEvents).
/// SQL does the filtering and counting; graph and text analysis run in memory on bounded result
/// sets. Every query is restricted to the caller's <see cref="AnalyticsScope"/>.
/// </summary>
public sealed class ChatAnalyticsService(KickGatewayDbContext db, IOptions<ChatAnalyticsOptions> options)
{
    private static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);
    private static readonly GraphOptions DefaultWeights = new();

    private static readonly HashSet<string> NonRoleBadges = new(StringComparer.Ordinal) { "subscriber", "sub_gifter" };

    private readonly ChatAnalyticsOptions _opts = options.Value;

    private static readonly Expression<Func<ChatMessageRecord, MessageView>> ToView = m => new MessageView(
        m.MessageId, m.ChannelSlug, m.CreatedAt, m.SenderUserId, m.SenderUsername, m.Content,
        m.ReplyToMessageId, m.ReplyToUserId, m.ReplyToUsername, false);

    // =====================================================================================
    // status / channels
    // =====================================================================================

    public async Task<AnalyticsStatus> StatusAsync(AnalyticsScope scope, CancellationToken ct)
    {
        var checkpoints = await db.AnalyticsCheckpoints.AsNoTracking().ToListAsync(ct);

        var webhook = checkpoints.FirstOrDefault(c => c.Name == ChatProjector.WebhookCheckpoint);
        var wt = webhook?.Position ?? DateTime.MinValue;
        var wk = webhook?.PositionKey ?? "";
        var types = ChatProjectionMapper.ProjectedEventTypes;
        var webhookPending = await db.ReceivedWebhooks.AsNoTracking()
            .LongCountAsync(x => types.Contains(x.EventType)
                                 && (x.ReceivedAt > wt || (x.ReceivedAt == wt && string.Compare(x.MessageId, wk) > 0)), ct);

        var realtime = checkpoints.FirstOrDefault(c => c.Name == ChatProjector.RealtimeCheckpoint);
        var rt = realtime?.Position ?? DateTime.MinValue;
        var rk = realtime?.PositionKey ?? "";
        var realtimePending = await db.ReceivedRealtimeEvents.AsNoTracking()
            .LongCountAsync(x => x.ReceivedAt > rt || (x.ReceivedAt == rt && string.Compare(x.DedupeKey, rk) > 0), ct);

        var messages = Messages(scope);
        return new AnalyticsStatus(
            ProjectionEnabled: _opts.Projection.Enabled,
            Projections: new[]
            {
                Projection("webhooks", webhook, webhookPending),
                Projection("realtime", realtime, realtimePending),
            },
            ChatMessages: await messages.LongCountAsync(ct),
            ChatterEvents: await Events(scope).LongCountAsync(ct),
            OldestMessageAt: await messages.MinAsync(m => (DateTime?)m.CreatedAt, ct),
            NewestMessageAt: await messages.MaxAsync(m => (DateTime?)m.CreatedAt, ct),
            ChannelsInScope: scope.Slugs?.OrderBy(s => s, StringComparer.Ordinal).ToList());

        static ProjectionStatus Projection(string source, AnalyticsCheckpoint? cp, long pending) => new(
            source,
            cp is null || cp.Position == DateTime.MinValue ? null : cp.Position,
            cp?.ProcessedCount ?? 0,
            pending,
            cp?.UpdatedAt);
    }

    public async Task<ChannelList> ChannelsAsync(AnalyticsScope scope, AnalyticsWindow window, CancellationToken ct)
    {
        var rows = await Messages(scope, null, window)
            .GroupBy(m => m.ChannelSlug)
            .Select(g => new
            {
                Slug = g.Key,
                Messages = g.Count(),
                Chatters = g.Select(m => m.SenderUserId).Distinct().Count(),
                First = g.Min(m => m.CreatedAt),
                Last = g.Max(m => m.CreatedAt),
            })
            .OrderByDescending(x => x.Messages)
            .ToListAsync(ct);
        return new ChannelList(window, rows.Select(r => new ChannelSummary(r.Slug, r.Messages, r.Chatters, r.First, r.Last)).ToList());
    }

    // =====================================================================================
    // channel overview + chatters
    // =====================================================================================

    public async Task<ChannelOverview> OverviewAsync(AnalyticsScope scope, string slug, AnalyticsWindow window, CancellationToken ct)
    {
        var q = Messages(scope, slug, window);
        var messages = await q.CountAsync(ct);
        var replies = await q.CountAsync(m => m.ReplyToMessageId != null, ct);
        var withMentions = await q.CountAsync(m => m.Mentions.Any(), ct);
        var chatters = await q.Select(m => m.SenderUserId).Distinct().CountAsync(ct);

        var hours = await q.GroupBy(m => m.CreatedAt.Hour).Select(g => new { Hour = g.Key, Count = g.Count() }).ToListAsync(ct);
        var byHour = new int[24];
        foreach (var h in hours) byHour[h.Hour] = h.Count;

        var daily = await q.GroupBy(m => m.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Messages = g.Count(), Chatters = g.Select(m => m.SenderUserId).Distinct().Count() })
            .OrderBy(x => x.Day)
            .ToListAsync(ct);

        // "New" = the chatter's first message ever in this channel falls inside the window.
        var newChatters = chatters;
        if (window.From is { } from)
        {
            var inWindow = q.Select(m => m.SenderUserId).Distinct();
            newChatters = await db.ChatMessages.AsNoTracking()
                .Where(m => m.ChannelSlug == slug && inWindow.Contains(m.SenderUserId))
                .GroupBy(m => m.SenderUserId)
                .Where(g => g.Min(m => m.CreatedAt) >= from)
                .CountAsync(ct);
        }

        var top = await ChattersAsync(scope, slug, window, "messages", 15, 0, ct);
        var events = await EventTotalsAsync(scope, slug, window, ct);

        return new ChannelOverview(
            slug, window, messages, chatters, newChatters, chatters - newChatters,
            Share(replies, messages), Share(withMentions, messages),
            byHour,
            daily.Select(d => new DailyActivity(d.Day, d.Messages, d.Chatters)).ToList(),
            top.Chatters,
            events);
    }

    public static readonly string[] ChatterSorts = { "messages", "active_days", "replies", "last_seen", "first_seen" };

    public async Task<ChatterPage> ChattersAsync(AnalyticsScope scope, string slug, AnalyticsWindow window,
        string sort, int limit, int offset, CancellationToken ct)
    {
        var q = Messages(scope, slug, window);
        var grouped = q.GroupBy(m => m.SenderUserId).Select(g => new ChatterAgg
        {
            UserId = g.Key,
            Messages = g.Count(),
            RepliesSent = g.Count(m => m.ReplyToMessageId != null),
            First = g.Min(m => m.CreatedAt),
            Last = g.Max(m => m.CreatedAt),
            ActiveDays = g.Select(m => m.CreatedAt.Date).Distinct().Count(),
        });

        var total = await grouped.CountAsync(ct);
        sort = ChatterSorts.Contains(sort) ? sort : "messages";
        var ordered = sort switch
        {
            "active_days" => grouped.OrderByDescending(x => x.ActiveDays).ThenByDescending(x => x.Messages),
            "replies" => grouped.OrderByDescending(x => x.RepliesSent).ThenByDescending(x => x.Messages),
            "last_seen" => grouped.OrderByDescending(x => x.Last),
            "first_seen" => grouped.OrderBy(x => x.First),
            _ => grouped.OrderByDescending(x => x.Messages),
        };
        var page = await ordered.ThenBy(x => x.UserId).Skip(offset).Take(limit).ToListAsync(ct);
        if (page.Count == 0) return new ChatterPage(slug, window, sort, total, offset, Array.Empty<ChatterSummary>());

        var ids = page.Select(p => p.UserId).ToList();
        var names = await LatestNamesAsync(q, ids, ct);

        var repliesReceived = await q.Where(m => m.ReplyToUserId != null && ids.Contains(m.ReplyToUserId))
            .GroupBy(m => m.ReplyToUserId!)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var lowerNames = names.Values.Select(n => n.ToLowerInvariant()).Distinct().ToList();
        var mentions = (await Mentions(scope, slug, window)
                .Where(x => lowerNames.Contains(x.MentionedUsername))
                .GroupBy(x => x.MentionedUsername)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Name, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var firstSeen = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChannelSlug == slug && ids.Contains(m.SenderUserId))
            .GroupBy(m => m.SenderUserId)
            .Select(g => new { Id = g.Key, First = g.Min(m => m.CreatedAt) })
            .ToDictionaryAsync(x => x.Id, x => x.First, ct);

        var list = page.Select(p =>
        {
            var name = names.GetValueOrDefault(p.UserId, p.UserId);
            var first = firstSeen.GetValueOrDefault(p.UserId, p.First);
            return new ChatterSummary(
                p.UserId, name, p.Messages, p.ActiveDays, p.RepliesSent,
                repliesReceived.GetValueOrDefault(p.UserId),
                mentions.GetValueOrDefault(name),
                p.First, p.Last, first,
                IsNewInWindow: window.From is null || first >= window.From);
        }).ToList();
        return new ChatterPage(slug, window, sort, total, offset, list);
    }

    public async Task<IReadOnlyList<ChatterSearchHit>> SearchChattersAsync(AnalyticsScope scope, string query, string? channel,
        int limit, CancellationToken ct)
    {
        var term = query.Trim().TrimStart('@');
        if (term.Length == 0) return Array.Empty<ChatterSearchHit>();

        var q = Messages(scope, channel);
        var rows = await q.Where(m => m.SenderUsername.Contains(term) || m.SenderUserId == term)
            .GroupBy(m => m.SenderUserId)
            .Select(g => new { Id = g.Key, Messages = g.Count(), Last = g.Max(m => m.CreatedAt) })
            .OrderByDescending(x => x.Messages)
            .Take(limit * 3)
            .ToListAsync(ct);
        var names = await LatestNamesAsync(q, rows.Select(r => r.Id).ToList(), ct);

        return rows
            .Select(r => new ChatterSearchHit(r.Id, names.GetValueOrDefault(r.Id, r.Id), r.Messages, r.Last))
            .OrderByDescending(h => h.Username.Equals(term, StringComparison.OrdinalIgnoreCase) || h.UserId == term)
            .ThenByDescending(h => h.Username.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(h => h.Messages)
            .Take(limit)
            .ToList();
    }

    // =====================================================================================
    // interaction graph
    // =====================================================================================

    public async Task<InteractionGraphView> GraphAsync(AnalyticsScope scope, string slug, AnalyticsWindow window,
        GraphOptions weights, int maxNodes, int maxEdges, CancellationToken ct)
    {
        var cap = Math.Max(1000, _opts.MaxGraphMessages);
        var rows = await Messages(scope, slug, window)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.MessageId)
            .Take(cap + 1)
            .Select(m => new GraphMessage(m.MessageId, m.SenderUserId, m.SenderUsername, m.CreatedAt, m.ReplyToUserId, m.ReplyToUsername))
            .ToListAsync(ct);
        var truncated = rows.Count > cap;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        rows.Reverse(); // chronological
        var effective = truncated && rows.Count > 0 ? window with { From = rows[0].CreatedAt } : window;

        var mentionRows = await Mentions(scope, slug, effective)
            .Select(x => new { x.MessageId, x.MentionedUsername })
            .ToListAsync(ct);
        var mentions = mentionRows
            .GroupBy(x => x.MessageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.MentionedUsername).ToList(), StringComparer.OrdinalIgnoreCase);

        var gifts = await Events(scope, slug, effective)
            .Where(e => e.Kind == ChatterEventKind.SubscriptionGift && e.UserId != "" && e.CounterpartUserId != null)
            .Select(e => new GraphGift(e.UserId, e.Username, e.CounterpartUserId, e.CounterpartUsername, e.Amount))
            .ToListAsync(ct);

        var broadcaster = await BroadcasterUserIdAsync(scope, slug, effective, ct);
        var graph = InteractionGraph.Build(rows, mentions, gifts, weights, broadcaster);

        var names = graph.Nodes.ToDictionary(n => n.Key, n => n.Username, StringComparer.Ordinal);
        var strength = graph.Nodes.ToDictionary(n => n.Key, n => n.Strength, StringComparer.Ordinal);
        var nodes = graph.Nodes.Take(Math.Clamp(maxNodes, 1, 500)).ToList();
        var keep = nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        var edges = graph.Edges
            .Where(e => keep.Contains(e.From) && keep.Contains(e.To))
            .Take(Math.Clamp(maxEdges, 1, 2000))
            .Select(e => new GraphEdgeView(e.From, names[e.From], e.To, names[e.To], e.Replies, e.Mentions, e.Gifts, e.Proximity, e.Weight))
            .ToList();
        var communities = graph.Communities
            .Where(c => c.MemberKeys.Count >= 2)
            .Take(25)
            .Select(c => new GraphCommunityView(
                c.Id,
                c.MemberKeys.Count,
                c.MemberKeys.OrderByDescending(k => strength[k]).Take(12).Select(k => names[k]).ToList(),
                c.InternalWeight,
                c.Cohesion))
            .ToList();

        var summary = new GraphSummaryView(
            Messages: graph.Messages,
            Chatters: graph.Chatters,
            ConnectedChatters: graph.Nodes.Count(n => n.Messages > 0),
            Edges: graph.Edges.Count,
            Density: graph.Density,
            Reciprocity: graph.Reciprocity,
            Modularity: graph.Modularity,
            TopHubs: graph.Nodes.OrderByDescending(n => n.InWeight).Take(5).Select(n => n.Username).ToList(),
            TopInitiators: graph.Nodes.OrderByDescending(n => n.OutWeight).Take(5).Select(n => n.Username).ToList(),
            TopConnectors: graph.Nodes.Where(n => n.Participation >= 0.3 && n.Partners >= 3)
                .OrderByDescending(n => n.Strength * n.Participation).Take(5).Select(n => n.Username).ToList());

        return new InteractionGraphView(slug, effective, truncated, summary, nodes, edges, communities, weights, GraphLegend);
    }

    private const string GraphLegend =
        "Directed edge From→To = From addressed To: Kick reply (replies), @mention (mentions), gifted sub (gifts), or — only in slow chat — " +
        "spoke right after To (proximity, split between the ≤3 people who spoke in the preceding window). weight = replies*replyWeight + " +
        "mentions*mentionWeight + gifts*giftWeight + proximity*proximityWeight. Node inWeight = attention received, outWeight = attention given, " +
        "partners/mutualPartners = distinct people interacted with (mutual = both directions). community = Louvain cluster id (1 = largest); " +
        "participation 0..1 = how evenly a node's ties spread across communities (high = bridge between groups). cohesion = share of a " +
        "community's tie weight that stays inside it. topHubs draw attention, topInitiators address others most, topConnectors bridge groups. " +
        "Nodes keyed '@name' were mentioned but never seen chatting.";

    // =====================================================================================
    // chatter profile
    // =====================================================================================

    public async Task<ChatterProfile?> ProfileAsync(AnalyticsScope scope, string user, string? channel, AnalyticsWindow window,
        CancellationToken ct)
    {
        var who = await ResolveUserAsync(scope, user, ct);
        if (who is null) return null;
        var id = who.UserId;

        var q = Messages(scope, channel, window).Where(m => m.SenderUserId == id);
        var total = await q.CountAsync(ct);
        var cap = Math.Max(100, _opts.MaxProfileMessages);
        var sample = await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.MessageId)
            .Take(cap)
            .Select(m => new ProfileRow
            {
                View = new MessageView(m.MessageId, m.ChannelSlug, m.CreatedAt, m.SenderUserId, m.SenderUsername, m.Content,
                    m.ReplyToMessageId, m.ReplyToUserId, m.ReplyToUsername, false),
                Color = m.SenderColor,
                IsVerified = m.SenderIsVerified,
                OwnChannelSlug = m.SenderChannelSlug,
            })
            .ToListAsync(ct);

        var allTime = Messages(scope, channel).Where(m => m.SenderUserId == id);
        var firstSeen = await allTime.MinAsync(m => (DateTime?)m.CreatedAt, ct);
        var lastSeen = await allTime.MaxAsync(m => (DateTime?)m.CreatedAt, ct);
        var activeDays = await q.Select(m => m.CreatedAt.Date).Distinct().CountAsync(ct);
        var knownNames = await KnownUsernamesAsync(scope, id, who.Username, ct);
        var namesLower = knownNames.Select(n => n.ToLowerInvariant()).Distinct().ToList();

        var presence = await q.GroupBy(m => m.ChannelSlug)
            .Select(g => new
            {
                Slug = g.Key,
                Messages = g.Count(),
                First = g.Min(m => m.CreatedAt),
                Last = g.Max(m => m.CreatedAt),
                Badges = g.OrderByDescending(m => m.CreatedAt).Select(m => m.SenderBadges).FirstOrDefault(),
            })
            .OrderByDescending(x => x.Messages)
            .ToListAsync(ct);

        var latest = sample.FirstOrDefault();
        return new ChatterProfile(
            UserId: id,
            Username: who.Username,
            KnownUsernames: knownNames,
            OwnChannelSlug: latest?.OwnChannelSlug,
            IsVerified: latest?.IsVerified ?? false,
            Color: latest?.Color,
            Window: window,
            Channel: channel,
            Activity: BuildActivity(total, sample.Select(s => s.View.CreatedAt).ToList(), firstSeen, lastSeen, activeDays),
            Channels: presence.Select(p =>
            {
                var badges = ChatBadges.Parse(p.Badges);
                return new ChannelPresence(
                    p.Slug, p.Messages, p.First, p.Last, badges,
                    badges.FirstOrDefault(b => b.Type == "subscriber")?.Count,
                    badges.Select(b => b.Type).Where(t => !NonRoleBadges.Contains(t)).Distinct().ToList());
            }).ToList(),
            Style: ChatTextStats.Compute(sample.Select(s => s.View.Content).ToList()),
            Social: await SocialAsync(scope, channel, window, id, namesLower, ct),
            Support: await SupportAsync(scope, channel, window, id, namesLower, ct),
            Moderation: await ModerationAsync(scope, channel, window, id, namesLower, q, ct),
            RecentMessages: await WithDeletedFlagsAsync(sample.Take(25).Select(s => s.View).ToList(), ct));
    }

    private static ChatterActivity BuildActivity(int total, List<DateTime> newestFirst, DateTime? firstSeen, DateTime? lastSeen, int activeDays)
    {
        var byHour = new int[24];
        var byWeekday = new int[7]; // 0 = Monday … 6 = Sunday
        foreach (var t in newestFirst)
        {
            byHour[t.Hour]++;
            byWeekday[((int)t.DayOfWeek + 6) % 7]++;
        }

        var sessions = 0;
        var sessionMinutes = 0.0;
        DateTime? start = null, prev = null;
        foreach (var t in Enumerable.Reverse(newestFirst))
        {
            if (prev is null || t - prev.Value > SessionGap)
            {
                if (start is not null) sessionMinutes += (prev!.Value - start.Value).TotalMinutes;
                sessions++;
                start = t;
            }
            prev = t;
        }
        if (start is not null) sessionMinutes += (prev!.Value - start.Value).TotalMinutes;

        var peak = byHour.Select((count, hour) => (count, hour)).Where(x => x.count > 0)
            .OrderByDescending(x => x.count).ThenBy(x => x.hour).Take(3).Select(x => x.hour).ToList();

        return new ChatterActivity(
            Messages: total,
            SampledMessages: newestFirst.Count,
            FirstSeenAt: firstSeen,
            LastSeenAt: lastSeen,
            ActiveDays: activeDays,
            Sessions: sessions,
            MessagesPerActiveDay: activeDays == 0 ? 0 : Math.Round(total / (double)activeDays, 1),
            AvgSessionMinutes: sessions == 0 ? 0 : Math.Round(sessionMinutes / sessions, 1),
            MessagesByHourUtc: byHour,
            MessagesByWeekdayUtc: byWeekday,
            PeakHoursUtc: peak);
    }

    private async Task<ChatterSocial> SocialAsync(AnalyticsScope scope, string? channel, AnalyticsWindow window, string id,
        List<string> namesLower, CancellationToken ct)
    {
        var inScope = Messages(scope, channel, window);
        var repliesTo = await inScope.Where(m => m.SenderUserId == id && m.ReplyToUserId != null && m.ReplyToUserId != id)
            .GroupBy(m => m.ReplyToUserId!)
            .Select(g => new Tally { Key = g.Key, Count = g.Count(), Name = g.Max(m => m.ReplyToUsername) })
            .ToListAsync(ct);
        var repliesFrom = await inScope.Where(m => m.ReplyToUserId == id && m.SenderUserId != id)
            .GroupBy(m => m.SenderUserId)
            .Select(g => new Tally { Key = g.Key, Count = g.Count(), Name = g.Max(m => m.SenderUsername) })
            .ToListAsync(ct);
        var mentionsTo = await Mentions(scope, channel, window)
            .Where(x => x.Message!.SenderUserId == id)
            .GroupBy(x => x.MentionedUsername)
            .Select(g => new Tally { Key = g.Key, Count = g.Count(), Name = g.Key })
            .ToListAsync(ct);
        var mentionsFrom = await Mentions(scope, channel, window)
            .Where(x => namesLower.Contains(x.MentionedUsername) && x.Message!.SenderUserId != id)
            .GroupBy(x => x.Message!.SenderUserId)
            .Select(g => new Tally { Key = g.Key, Count = g.Count(), Name = g.Max(x => x.Message!.SenderUsername) })
            .ToListAsync(ct);
        var giftsTo = await Events(scope, channel, window)
            .Where(e => e.Kind == ChatterEventKind.SubscriptionGift && e.UserId == id && e.CounterpartUserId != null)
            .GroupBy(e => e.CounterpartUserId!)
            .Select(g => new Tally { Key = g.Key, Count = g.Sum(e => e.Amount), Name = g.Max(e => e.CounterpartUsername) })
            .ToListAsync(ct);
        var giftsFrom = await Events(scope, channel, window)
            .Where(e => e.Kind == ChatterEventKind.SubscriptionGift && e.CounterpartUserId == id && e.UserId != "")
            .GroupBy(e => e.UserId)
            .Select(g => new Tally { Key = g.Key, Count = g.Sum(e => e.Amount), Name = g.Max(e => e.Username) })
            .ToListAsync(ct);

        // Mentions are by username — map to ids where the name has chatted.
        var mentioned = mentionsTo.Select(x => x.Key).ToList();
        var nameToId = mentioned.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : (await Messages(scope)
                    .Where(m => mentioned.Contains(m.SenderUsername))
                    .GroupBy(m => m.SenderUsername)
                    .Select(g => new { Name = g.Key, Id = g.Max(m => m.SenderUserId) })
                    .ToListAsync(ct))
                .Where(x => !string.IsNullOrEmpty(x.Id))
                .ToDictionary(x => x.Name, x => x.Id!, StringComparer.OrdinalIgnoreCase);

        var partners = new Dictionary<string, PartnerAcc>(StringComparer.Ordinal);
        PartnerAcc P(string key, string? name)
        {
            if (!partners.TryGetValue(key, out var p)) partners[key] = p = new PartnerAcc { Key = key };
            if (!string.IsNullOrEmpty(name) && (p.Name is null || p.Name.StartsWith('@'))) p.Name = name;
            return p;
        }
        foreach (var t in repliesTo) P(t.Key, t.Name).RepliesTo += t.Count;
        foreach (var t in repliesFrom) P(t.Key, t.Name).RepliesFrom += t.Count;
        foreach (var t in mentionsFrom) P(t.Key, t.Name).MentionsFrom += t.Count;
        foreach (var t in giftsTo) P(t.Key, t.Name).GiftsTo += t.Count;
        foreach (var t in giftsFrom) P(t.Key, t.Name).GiftsFrom += t.Count;
        foreach (var t in mentionsTo)
        {
            var key = nameToId.TryGetValue(t.Key, out var uid) ? uid : "@" + t.Key;
            if (key == id) continue;
            P(key, partners.TryGetValue(key, out var existing) ? existing.Name : t.Key).MentionsTo += t.Count;
        }

        var w = DefaultWeights;
        var top = partners.Values
            .Select(p => new PartnerView(
                p.Key.StartsWith('@') ? null : p.Key,
                p.Name ?? p.Key,
                Math.Round((p.RepliesTo + p.RepliesFrom) * w.ReplyWeight + (p.MentionsTo + p.MentionsFrom) * w.MentionWeight
                           + (p.GiftsTo + p.GiftsFrom) * w.GiftWeight, 2),
                p.RepliesTo, p.RepliesFrom, p.MentionsTo, p.MentionsFrom, p.GiftsTo, p.GiftsFrom))
            .OrderByDescending(p => p.Weight).ThenBy(p => p.Username, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return new ChatterSocial(
            DistinctPartners: partners.Count,
            RepliesSent: repliesTo.Sum(t => t.Count),
            RepliesReceived: repliesFrom.Sum(t => t.Count),
            MentionsSent: mentionsTo.Sum(t => t.Count),
            MentionsReceived: mentionsFrom.Sum(t => t.Count),
            TopPartners: top);
    }

    private async Task<ChatterSupport> SupportAsync(AnalyticsScope scope, string? channel, AnalyticsWindow window, string id,
        List<string> namesLower, CancellationToken ct)
    {
        var kinds = new[]
        {
            ChatterEventKind.Follow, ChatterEventKind.SubscriptionNew, ChatterEventKind.SubscriptionRenewal,
            ChatterEventKind.SubscriptionGift, ChatterEventKind.KicksGift, ChatterEventKind.RewardRedemption,
        };
        var ev = await ActorEvents(scope, channel, window, id, namesLower)
            .Where(e => kinds.Contains(e.Kind))
            .OrderByDescending(e => e.OccurredAt)
            .Take(5000)
            .ToListAsync(ct);

        var giftsReceived = await Events(scope, channel, window)
            .Where(e => e.Kind == ChatterEventKind.SubscriptionGift
                        && (e.CounterpartUserId == id || (e.CounterpartUserId == null && namesLower.Contains(e.CounterpartUsername!))))
            .SumAsync(e => (int?)e.Amount, ct) ?? 0;

        return new ChatterSupport(
            Follows: ev.Where(e => e.Kind == ChatterEventKind.Follow)
                .GroupBy(e => e.ChannelSlug)
                .Select(g => new ChannelFollowView(g.Key, g.Min(e => e.OccurredAt)))
                .OrderBy(f => f.At).ToList(),
            Subscriptions: ev.Where(e => e.Kind is ChatterEventKind.SubscriptionNew or ChatterEventKind.SubscriptionRenewal)
                .Take(50)
                .Select(e => new SubscriptionView(e.ChannelSlug, e.Kind == ChatterEventKind.SubscriptionNew ? "new" : "renewal", e.Amount, e.OccurredAt))
                .ToList(),
            GiftedSubsGiven: ev.Where(e => e.Kind == ChatterEventKind.SubscriptionGift).Sum(e => e.Amount),
            GiftedSubsReceived: giftsReceived,
            KicksGiven: ev.Where(e => e.Kind == ChatterEventKind.KicksGift).Sum(e => e.Amount),
            Redemptions: ev.Where(e => e.Kind == ChatterEventKind.RewardRedemption)
                .GroupBy(e => e.Detail ?? "(untitled)")
                .Select(g => new TermCount(g.Key, g.Select(e => e.RefId ?? e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
                .OrderByDescending(t => t.Count).ThenBy(t => t.Term, StringComparer.Ordinal)
                .ToList());
    }

    private async Task<ChatterModeration> ModerationAsync(AnalyticsScope scope, string? channel, AnalyticsWindow window, string id,
        List<string> namesLower, IQueryable<ChatMessageRecord> ownMessages, CancellationToken ct)
    {
        var modKinds = new[] { ChatterEventKind.Ban, ChatterEventKind.Timeout, ChatterEventKind.Unban };
        var received = await ActorEvents(scope, channel, window, id, namesLower)
            .Where(e => modKinds.Contains(e.Kind))
            .OrderByDescending(e => e.OccurredAt)
            .Take(500)
            .ToListAsync(ct);
        var issuedQ = Events(scope, channel, window).Where(e => modKinds.Contains(e.Kind) && e.CounterpartUserId == id);
        var issuedCount = await issuedQ.CountAsync(ct);
        var issued = await issuedQ.OrderByDescending(e => e.OccurredAt).Take(15).ToListAsync(ct);

        var deleted = await ownMessages.CountAsync(m => db.ChatterEvents.Any(e =>
            e.Kind == ChatterEventKind.MessageDeleted && e.RefId == m.MessageId), ct);

        var recent = received.Select(e => ToModeration(e, "received"))
            .Concat(issued.Select(e => ToModeration(e, "issued")))
            .OrderByDescending(v => v.At)
            .Take(20)
            .ToList();

        return new ChatterModeration(
            BansReceived: received.Count(e => e.Kind == ChatterEventKind.Ban),
            TimeoutsReceived: received.Count(e => e.Kind == ChatterEventKind.Timeout),
            MessagesDeleted: deleted,
            ActionsIssued: issuedCount,
            Recent: recent);
    }

    private static ModerationView ToModeration(ChatterEvent e, string direction) => new(
        e.ChannelSlug,
        e.Kind switch { ChatterEventKind.Ban => "ban", ChatterEventKind.Timeout => "timeout", ChatterEventKind.Unban => "unban", _ => e.Kind.ToString() },
        direction, e.OccurredAt, e.UserId, e.Username, e.CounterpartUsername, e.Detail, e.ExpiresAt);

    // =====================================================================================
    // relationship between two chatters
    // =====================================================================================

    public async Task<ChatterRelationship?> RelationshipAsync(AnalyticsScope scope, ChatterRef a, ChatterRef b, string? channel,
        AnalyticsWindow window, CancellationToken ct)
    {
        var namesA = (await KnownUsernamesAsync(scope, a.UserId, a.Username, ct)).Select(n => n.ToLowerInvariant()).Distinct().ToList();
        var namesB = (await KnownUsernamesAsync(scope, b.UserId, b.Username, ct)).Select(n => n.ToLowerInvariant()).Distinct().ToList();

        var ab = await DirectionAsync(scope, channel, window, a.UserId, b.UserId, namesB, ct);
        var ba = await DirectionAsync(scope, channel, window, b.UserId, a.UserId, namesA, ct);

        var q = Messages(scope, channel, window);
        var channelsA = await q.Where(m => m.SenderUserId == a.UserId).Select(m => m.ChannelSlug).Distinct().ToListAsync(ct);
        var channelsB = await q.Where(m => m.SenderUserId == b.UserId).Select(m => m.ChannelSlug).Distinct().ToListAsync(ct);
        var daysA = await q.Where(m => m.SenderUserId == a.UserId).Select(m => m.CreatedAt.Date).Distinct().ToListAsync(ct);
        var daysB = await q.Where(m => m.SenderUserId == b.UserId).Select(m => m.CreatedAt.Date).Distinct().ToListAsync(ct);

        var modKinds = new[] { ChatterEventKind.Ban, ChatterEventKind.Timeout, ChatterEventKind.Unban };
        var moderation = await Events(scope, channel, window)
            .Where(e => modKinds.Contains(e.Kind)
                        && ((e.UserId == a.UserId && e.CounterpartUserId == b.UserId) || (e.UserId == b.UserId && e.CounterpartUserId == a.UserId)))
            .OrderByDescending(e => e.OccurredAt)
            .Take(20)
            .ToListAsync(ct);

        var exchanges = await ExchangesAsync(scope, channel, window, a.UserId, b.UserId, namesA, namesB, ct);

        var total = ab.Weight + ba.Weight;
        var balance = total <= 0 ? "none"
            : ab.Weight / total >= 0.65 ? $"mostly {a.Username} → {b.Username}"
            : ab.Weight / total <= 0.35 ? $"mostly {b.Username} → {a.Username}"
            : "mutual";

        return new ChatterRelationship(
            a, b, window, channel, ab, ba, balance,
            channelsA.Intersect(channelsB, StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            daysA.Count, daysB.Count, daysA.Intersect(daysB).Count(),
            moderation.Select(e => ToModeration(e, e.UserId == a.UserId ? $"{b.Username} → {a.Username}" : $"{a.Username} → {b.Username}")).ToList(),
            exchanges);
    }

    private async Task<DirectionStats> DirectionAsync(AnalyticsScope scope, string? channel, AnalyticsWindow window,
        string from, string to, List<string> toNamesLower, CancellationToken ct)
    {
        var replies = Messages(scope, channel, window).Where(m => m.SenderUserId == from && m.ReplyToUserId == to);
        var replyCount = await replies.CountAsync(ct);
        var mentions = Mentions(scope, channel, window)
            .Where(x => x.Message!.SenderUserId == from && toNamesLower.Contains(x.MentionedUsername));
        var mentionCount = await mentions.Select(x => x.MessageId).Distinct().CountAsync(ct);
        var gifts = await Events(scope, channel, window)
            .Where(e => e.Kind == ChatterEventKind.SubscriptionGift && e.UserId == from && e.CounterpartUserId == to)
            .SumAsync(e => (int?)e.Amount, ct) ?? 0;

        var latencies = await replies.OrderByDescending(m => m.CreatedAt).Take(500)
            .Join(db.ChatMessages, r => r.ReplyToMessageId, p => p.MessageId, (r, p) => new { r.CreatedAt, ParentAt = p.CreatedAt })
            .ToListAsync(ct);
        var seconds = latencies.Select(l => (l.CreatedAt - l.ParentAt).TotalSeconds).Where(s => s >= 0).OrderBy(s => s).ToList();
        double? median = seconds.Count == 0 ? null
            : Math.Round(seconds.Count % 2 == 1 ? seconds[seconds.Count / 2] : (seconds[seconds.Count / 2 - 1] + seconds[seconds.Count / 2]) / 2, 1);

        var lastReply = await replies.MaxAsync(m => (DateTime?)m.CreatedAt, ct);
        var lastMention = await mentions.MaxAsync(x => (DateTime?)x.Message!.CreatedAt, ct);
        DateTime? last = lastReply > lastMention || lastMention is null ? lastReply : lastMention;

        var w = DefaultWeights;
        return new DirectionStats(replyCount, mentionCount, gifts,
            Math.Round(replyCount * w.ReplyWeight + mentionCount * w.MentionWeight + gifts * w.GiftWeight, 2),
            median, last);
    }

    private async Task<IReadOnlyList<ExchangeView>> ExchangesAsync(AnalyticsScope scope, string? channel, AnalyticsWindow window,
        string a, string b, List<string> namesA, List<string> namesB, CancellationToken ct)
    {
        const int take = 40;
        var q = Messages(scope, channel, window);
        var replies = await q
            .Where(m => (m.SenderUserId == a && m.ReplyToUserId == b) || (m.SenderUserId == b && m.ReplyToUserId == a))
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.MessageId)
            .Take(take).Select(ToView).ToListAsync(ct);
        var mentionIds = Mentions(scope, channel, window)
            .Where(x => (x.Message!.SenderUserId == a && namesB.Contains(x.MentionedUsername))
                        || (x.Message!.SenderUserId == b && namesA.Contains(x.MentionedUsername)))
            .Select(x => x.MessageId);
        var mentions = await q.Where(m => mentionIds.Contains(m.MessageId))
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.MessageId)
            .Take(take).Select(ToView).ToListAsync(ct);

        var merged = replies.Concat(mentions)
            .DistinctBy(m => m.MessageId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .ToList();
        merged = await WithDeletedFlagsAsync(merged, ct);

        var parentIds = merged.Where(m => m.ReplyToMessageId is not null).Select(m => m.ReplyToMessageId!).Distinct().ToList();
        var parents = parentIds.Count == 0
            ? new Dictionary<string, MessageView>(StringComparer.OrdinalIgnoreCase)
            : (await WithDeletedFlagsAsync(await Messages(scope).Where(m => parentIds.Contains(m.MessageId)).Select(ToView).ToListAsync(ct), ct))
                .ToDictionary(m => m.MessageId, StringComparer.OrdinalIgnoreCase);

        return merged.Select(m =>
        {
            var isReply = m.ReplyToUserId == a || m.ReplyToUserId == b;
            return new ExchangeView(isReply ? "reply" : "mention", m,
                isReply && m.ReplyToMessageId is not null ? parents.GetValueOrDefault(m.ReplyToMessageId) : null);
        }).ToList();
    }

    // =====================================================================================
    // messages
    // =====================================================================================

    public async Task<MessagePage> MessagesAsync(AnalyticsScope scope, string? channel, string? userId, string? text,
        AnalyticsWindow window, string? cursor, int limit, CancellationToken ct)
    {
        var q = Messages(scope, channel, window);
        if (userId is not null) q = q.Where(m => m.SenderUserId == userId);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var t = text.Trim();
            q = q.Where(m => m.Content.Contains(t));
        }
        if (TryParseCursor(cursor, out var at, out var id))
            q = q.Where(m => m.CreatedAt < at || (m.CreatedAt == at && string.Compare(m.MessageId, id) < 0));

        var rows = await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.MessageId)
            .Take(limit + 1).Select(ToView).ToListAsync(ct);
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = MakeCursor(rows[^1]);
        }
        return new MessagePage(await WithDeletedFlagsAsync(rows, ct), next);
    }

    public async Task<MessageContext?> ContextAsync(AnalyticsScope scope, string messageId, int before, int after, CancellationToken ct)
    {
        var msg = await Messages(scope).Where(m => m.MessageId == messageId).Select(ToView).FirstOrDefaultAsync(ct);
        if (msg is null) return null;

        var chain = new List<MessageView>();
        var parentId = msg.ReplyToMessageId;
        for (var depth = 0; depth < 5 && parentId is not null; depth++)
        {
            var id = parentId;
            var parent = await Messages(scope).Where(m => m.MessageId == id).Select(ToView).FirstOrDefaultAsync(ct);
            if (parent is null) break;
            chain.Insert(0, parent);
            parentId = parent.ReplyToMessageId;
        }

        var replies = await Messages(scope).Where(m => m.ReplyToMessageId == messageId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.MessageId).Take(50).Select(ToView).ToListAsync(ct);

        var t = msg.CreatedAt;
        var key = msg.MessageId;
        var channel = Messages(scope, msg.Channel);
        var earlier = await channel.Where(m => m.CreatedAt < t || (m.CreatedAt == t && string.Compare(m.MessageId, key) < 0))
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.MessageId)
            .Take(before).Select(ToView).ToListAsync(ct);
        earlier.Reverse();
        var later = await channel.Where(m => m.CreatedAt > t || (m.CreatedAt == t && string.Compare(m.MessageId, key) > 0))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.MessageId)
            .Take(after).Select(ToView).ToListAsync(ct);

        var all = await WithDeletedFlagsAsync(new[] { msg }.Concat(chain).Concat(replies).Concat(earlier).Concat(later).ToList(), ct);
        var flagged = all.Where(m => m.Deleted).Select(m => m.MessageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<MessageView> F(IEnumerable<MessageView> xs) => xs.Select(m => flagged.Contains(m.MessageId) ? m with { Deleted = true } : m).ToList();

        return new MessageContext(F(new[] { msg })[0], F(chain), F(replies), F(earlier), F(later));
    }

    // =====================================================================================
    // users
    // =====================================================================================

    /// <summary>Resolves a Kick user id or (current or former) username to a chatter seen in scope.</summary>
    public async Task<ChatterRef?> ResolveUserAsync(AnalyticsScope scope, string user, CancellationToken ct)
    {
        var u = user.Trim().TrimStart('@');
        if (u.Length == 0) return null;

        string? id = null;
        if (u.All(char.IsAsciiDigit))
        {
            var seen = await Messages(scope).AnyAsync(m => m.SenderUserId == u, ct)
                       || await Events(scope).AnyAsync(e => e.UserId == u, ct);
            if (seen) id = u;
        }
        id ??= await Messages(scope).Where(m => m.SenderUsername == u)
            .OrderByDescending(m => m.CreatedAt).Select(m => m.SenderUserId).FirstOrDefaultAsync(ct);
        id ??= await Events(scope).Where(e => e.Username == u && e.UserId != "")
            .OrderByDescending(e => e.OccurredAt).Select(e => e.UserId).FirstOrDefaultAsync(ct);
        if (id is null) return null;

        var name = await Messages(scope).Where(m => m.SenderUserId == id)
                       .OrderByDescending(m => m.CreatedAt).Select(m => m.SenderUsername).FirstOrDefaultAsync(ct)
                   ?? await Events(scope).Where(e => e.UserId == id)
                       .OrderByDescending(e => e.OccurredAt).Select(e => e.Username).FirstOrDefaultAsync(ct)
                   ?? u;
        return new ChatterRef(id, name);
    }

    private async Task<List<string>> KnownUsernamesAsync(AnalyticsScope scope, string id, string current, CancellationToken ct)
    {
        var fromChat = await Messages(scope).Where(m => m.SenderUserId == id).Select(m => m.SenderUsername).Distinct().Take(20).ToListAsync(ct);
        var fromEvents = await Events(scope).Where(e => e.UserId == id && e.Username != "").Select(e => e.Username).Distinct().Take(20).ToListAsync(ct);
        return new[] { current }.Concat(fromChat).Concat(fromEvents)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<Dictionary<string, string>> LatestNamesAsync(IQueryable<ChatMessageRecord> q, List<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = await q.Where(m => ids.Contains(m.SenderUserId))
            .GroupBy(m => m.SenderUserId)
            .Select(g => new { Id = g.Key, Name = g.OrderByDescending(m => m.CreatedAt).Select(m => m.SenderUsername).FirstOrDefault() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Name ?? r.Id, StringComparer.Ordinal);
    }

    private async Task<string?> BroadcasterUserIdAsync(AnalyticsScope scope, string slug, AnalyticsWindow window, CancellationToken ct)
    {
        var id = await db.Broadcasters.AsNoTracking().Where(b => b.ChannelSlug == slug && b.KickUserId != "")
            .Select(b => b.KickUserId).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(id)) return id;
        // Realtime-only channel: the streamer carries a "broadcaster" badge when they chat.
        return await Messages(scope, slug, window)
            .Where(m => m.SenderBadges != null && m.SenderBadges.Contains("broadcaster:"))
            .Select(m => m.SenderUserId).FirstOrDefaultAsync(ct);
    }

    private async Task<ChannelEventTotals> EventTotalsAsync(AnalyticsScope scope, string slug, AnalyticsWindow window, CancellationToken ct)
    {
        var q = Events(scope, slug, window);
        var rows = await q.GroupBy(e => e.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count(), Amount = g.Sum(e => e.Amount) })
            .ToListAsync(ct);
        var redemptions = await q.Where(e => e.Kind == ChatterEventKind.RewardRedemption)
            .Select(e => e.RefId ?? e.Id).Distinct().CountAsync(ct);

        int Count(ChatterEventKind k) => rows.FirstOrDefault(r => r.Kind == k)?.Count ?? 0;
        int Amount(ChatterEventKind k) => rows.FirstOrDefault(r => r.Kind == k)?.Amount ?? 0;
        return new ChannelEventTotals(
            Follows: Count(ChatterEventKind.Follow),
            NewSubscriptions: Count(ChatterEventKind.SubscriptionNew),
            Renewals: Count(ChatterEventKind.SubscriptionRenewal),
            GiftedSubscriptions: Amount(ChatterEventKind.SubscriptionGift),
            KicksGifted: Amount(ChatterEventKind.KicksGift),
            RewardRedemptions: redemptions,
            Bans: Count(ChatterEventKind.Ban),
            Timeouts: Count(ChatterEventKind.Timeout),
            MessagesDeleted: Count(ChatterEventKind.MessageDeleted));
    }

    // =====================================================================================
    // base queries + helpers
    // =====================================================================================

    private IQueryable<ChatMessageRecord> Messages(AnalyticsScope scope, string? channel = null, AnalyticsWindow? window = null)
    {
        IQueryable<ChatMessageRecord> q = db.ChatMessages.AsNoTracking();
        if (scope.Slugs is { } slugs)
        {
            var allowed = slugs.ToList();
            q = q.Where(m => allowed.Contains(m.ChannelSlug));
        }
        if (channel is not null) q = q.Where(m => m.ChannelSlug == channel);
        if (window?.From is { } from) q = q.Where(m => m.CreatedAt >= from);
        if (window is not null)
        {
            var to = window.To;
            q = q.Where(m => m.CreatedAt < to);
        }
        return q;
    }

    private IQueryable<ChatterEvent> Events(AnalyticsScope scope, string? channel = null, AnalyticsWindow? window = null)
    {
        IQueryable<ChatterEvent> q = db.ChatterEvents.AsNoTracking();
        if (scope.Slugs is { } slugs)
        {
            var allowed = slugs.ToList();
            q = q.Where(e => allowed.Contains(e.ChannelSlug));
        }
        if (channel is not null) q = q.Where(e => e.ChannelSlug == channel);
        if (window?.From is { } from) q = q.Where(e => e.OccurredAt >= from);
        if (window is not null)
        {
            var to = window.To;
            q = q.Where(e => e.OccurredAt < to);
        }
        return q;
    }

    /// <summary>Events where the chatter is the actor — by id, or by username for realtime rows whose id couldn't be resolved.</summary>
    private IQueryable<ChatterEvent> ActorEvents(AnalyticsScope scope, string? channel, AnalyticsWindow window, string id, List<string> namesLower) =>
        Events(scope, channel, window).Where(e => e.UserId == id || (e.UserId == "" && namesLower.Contains(e.Username)));

    private IQueryable<ChatMention> Mentions(AnalyticsScope scope, string? channel, AnalyticsWindow? window)
    {
        IQueryable<ChatMention> q = db.ChatMentions.AsNoTracking();
        if (scope.Slugs is { } slugs)
        {
            var allowed = slugs.ToList();
            q = q.Where(x => allowed.Contains(x.Message!.ChannelSlug));
        }
        if (channel is not null) q = q.Where(x => x.Message!.ChannelSlug == channel);
        if (window?.From is { } from) q = q.Where(x => x.Message!.CreatedAt >= from);
        if (window is not null)
        {
            var to = window.To;
            q = q.Where(x => x.Message!.CreatedAt < to);
        }
        return q;
    }

    private async Task<List<MessageView>> WithDeletedFlagsAsync(List<MessageView> list, CancellationToken ct)
    {
        if (list.Count == 0) return list;
        var ids = list.Select(m => m.MessageId).Distinct().ToList();
        var deleted = (await db.ChatterEvents.AsNoTracking()
                .Where(e => e.Kind == ChatterEventKind.MessageDeleted && e.RefId != null && ids.Contains(e.RefId))
                .Select(e => e.RefId!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return deleted.Count == 0 ? list : list.Select(m => deleted.Contains(m.MessageId) ? m with { Deleted = true } : m).ToList();
    }

    private static double Share(int part, int total) => total == 0 ? 0 : Math.Round(part / (double)total, 3);

    internal static string MakeCursor(MessageView m) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{m.CreatedAt.Ticks}|{m.MessageId}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryParseCursor(string? cursor, out DateTime at, out string messageId)
    {
        at = default;
        messageId = "";
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks)) return false;
            at = new DateTime(ticks, DateTimeKind.Utc);
            messageId = parts[1];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class ChatterAgg
    {
        public string UserId { get; init; } = "";
        public int Messages { get; init; }
        public int RepliesSent { get; init; }
        public DateTime First { get; init; }
        public DateTime Last { get; init; }
        public int ActiveDays { get; init; }
    }

    private sealed class ProfileRow
    {
        public required MessageView View { get; init; }
        public string? Color { get; init; }
        public bool IsVerified { get; init; }
        public string? OwnChannelSlug { get; init; }
    }

    private sealed class Tally
    {
        public string Key { get; init; } = "";
        public int Count { get; init; }
        public string? Name { get; init; }
    }

    private sealed class PartnerAcc
    {
        public string Key = "";
        public string? Name;
        public int RepliesTo, RepliesFrom, MentionsTo, MentionsFrom, GiftsTo, GiftsFrom;
    }
}
