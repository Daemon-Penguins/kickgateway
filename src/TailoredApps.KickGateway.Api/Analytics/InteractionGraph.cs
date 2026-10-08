namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>Minimal message shape the graph needs (chronological input).</summary>
public sealed record GraphMessage(
    string MessageId,
    string SenderUserId,
    string SenderUsername,
    DateTime CreatedAt,
    string? ReplyToUserId,
    string? ReplyToUsername);

/// <summary>A gifted sub (gifter → giftee).</summary>
public sealed record GraphGift(string GifterUserId, string GifterUsername, string? GifteeUserId, string? GifteeUsername, int Count);

/// <summary>
/// Signal weights. Explicit signals (reply, @mention, gift) are strong; proximity is the implicit
/// "answered right after you" signal and only fires in slow chat (see <see cref="ProximityMaxSpeakers"/>),
/// where turn-taking actually means conversation instead of co-presence.
/// </summary>
public sealed record GraphOptions
{
    public double ReplyWeight { get; init; } = 3;
    public double MentionWeight { get; init; } = 2;
    public double GiftWeight { get; init; } = 2;
    public double ProximityWeight { get; init; } = 0.5;
    public bool IncludeProximity { get; init; } = true;
    /// <summary>How far back a message looks for the speakers it may be answering.</summary>
    public int ProximityWindowSeconds { get; init; } = 20;
    /// <summary>Proximity only counts when at most this many other people spoke inside the window;
    /// the unit of weight is split between them. Busier chat → no proximity edge at all.</summary>
    public int ProximityMaxSpeakers { get; init; } = 3;
}

public sealed record GraphNodeResult(
    string Key,
    string? UserId,
    string Username,
    bool IsBroadcaster,
    int Messages,
    double OutWeight,
    double InWeight,
    int RepliesSent,
    int RepliesReceived,
    int MentionsSent,
    int MentionsReceived,
    int Partners,
    int MutualPartners,
    int Community,
    double Participation)
{
    public double Strength => OutWeight + InWeight;
}

public sealed record GraphEdgeResult(
    string From,
    string To,
    int Replies,
    int Mentions,
    int Gifts,
    double Proximity,
    double Weight);

public sealed record GraphCommunityResult(int Id, IReadOnlyList<string> MemberKeys, double InternalWeight, double Cohesion);

public sealed record InteractionGraphResult(
    int Messages,
    int Chatters,
    IReadOnlyList<GraphNodeResult> Nodes,
    IReadOnlyList<GraphEdgeResult> Edges,
    IReadOnlyList<GraphCommunityResult> Communities,
    double Modularity,
    double Reciprocity,
    double Density);

/// <summary>
/// Pure builder for the chatter interaction graph. Directed edge A→B = "A addressed B" (replied
/// to, @mentioned, gifted a sub to, or — weakly — answered right after). Nodes are keyed by Kick
/// user id; mentions of usernames that never appear with an id become <c>@name</c> nodes.
/// Communities come from Louvain modularity optimisation on the undirected (A→B + B→A) weights.
/// </summary>
public static class InteractionGraph
{
    public static InteractionGraphResult Build(
        IReadOnlyList<GraphMessage> messages,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mentionsByMessageId,
        IReadOnlyList<GraphGift> gifts,
        GraphOptions options,
        string? broadcasterUserId = null)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);      // key → display name
        var nameToKey = new Dictionary<string, string>(StringComparer.Ordinal);  // lowercase username → key
        var sent = new Dictionary<string, int>(StringComparer.Ordinal);

        void Learn(string? userId, string? username)
        {
            if (string.IsNullOrEmpty(userId)) return;
            if (!string.IsNullOrEmpty(username))
            {
                names[userId] = username;
                nameToKey[username.ToLowerInvariant()] = userId;
            }
            else names.TryAdd(userId, userId);
        }

        foreach (var m in messages)
        {
            Learn(m.ReplyToUserId, m.ReplyToUsername);
            Learn(m.SenderUserId, m.SenderUsername);
        }
        foreach (var g in gifts)
        {
            Learn(g.GifteeUserId, g.GifteeUsername);
            Learn(g.GifterUserId, g.GifterUsername);
        }

        string ResolveName(string lower)
        {
            if (nameToKey.TryGetValue(lower, out var key)) return key;
            key = "@" + lower;
            names.TryAdd(key, lower);
            return key;
        }

        var edges = new Dictionary<(string From, string To), EdgeAcc>();
        EdgeAcc Edge(string from, string to)
        {
            if (!edges.TryGetValue((from, to), out var acc)) edges[(from, to)] = acc = new EdgeAcc();
            return acc;
        }

        var window = TimeSpan.FromSeconds(Math.Max(1, options.ProximityWindowSeconds));
        var recent = new LinkedList<(DateTime At, string Key)>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var recentSpeakers = new List<string>();

        foreach (var m in messages)
        {
            var sender = m.SenderUserId;
            sent[sender] = sent.GetValueOrDefault(sender) + 1;
            targets.Clear();

            if (!string.IsNullOrEmpty(m.ReplyToUserId) && m.ReplyToUserId != sender)
            {
                Edge(sender, m.ReplyToUserId).Replies++;
                targets.Add(m.ReplyToUserId);
            }

            if (mentionsByMessageId.TryGetValue(m.MessageId, out var mentioned))
            {
                foreach (var name in mentioned)
                {
                    var key = ResolveName(name.ToLowerInvariant());
                    // A reply that also @s its target is one act of addressing, not two.
                    if (key == sender || !targets.Add(key)) continue;
                    Edge(sender, key).Mentions++;
                }
            }

            while (recent.First is { } first && m.CreatedAt - first.Value.At > window)
                recent.RemoveFirst();

            if (options.IncludeProximity && targets.Count == 0)
            {
                recentSpeakers.Clear();
                for (var node = recent.Last; node is not null; node = node.Previous)
                {
                    var k = node.Value.Key;
                    if (k != sender && !recentSpeakers.Contains(k)) recentSpeakers.Add(k);
                    if (recentSpeakers.Count > options.ProximityMaxSpeakers) break;
                }
                if (recentSpeakers.Count > 0 && recentSpeakers.Count <= options.ProximityMaxSpeakers)
                {
                    var share = 1.0 / recentSpeakers.Count;
                    foreach (var k in recentSpeakers) Edge(sender, k).Proximity += share;
                }
            }

            recent.AddLast((m.CreatedAt, sender));
        }

        foreach (var g in gifts)
        {
            if (string.IsNullOrEmpty(g.GifterUserId) || string.IsNullOrEmpty(g.GifteeUserId) || g.GifterUserId == g.GifteeUserId) continue;
            Edge(g.GifterUserId, g.GifteeUserId).Gifts += Math.Max(1, g.Count);
        }

        // === edges → weights ===
        var edgeList = new List<GraphEdgeResult>(edges.Count);
        foreach (var ((from, to), a) in edges)
        {
            var w = a.Replies * options.ReplyWeight + a.Mentions * options.MentionWeight
                    + a.Gifts * options.GiftWeight + a.Proximity * options.ProximityWeight;
            if (w <= 0) continue;
            edgeList.Add(new GraphEdgeResult(from, to, a.Replies, a.Mentions, a.Gifts, Math.Round(a.Proximity, 3), Math.Round(w, 3)));
        }

        // === node metrics ===
        var nodeKeys = edgeList.SelectMany(e => new[] { e.From, e.To }).Distinct(StringComparer.Ordinal).ToList();
        var outW = new Dictionary<string, double>(StringComparer.Ordinal);
        var inW = new Dictionary<string, double>(StringComparer.Ordinal);
        var neighbours = nodeKeys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var directed = new HashSet<(string, string)>();
        foreach (var e in edgeList)
        {
            outW[e.From] = outW.GetValueOrDefault(e.From) + e.Weight;
            inW[e.To] = inW.GetValueOrDefault(e.To) + e.Weight;
            neighbours[e.From].Add(e.To);
            neighbours[e.To].Add(e.From);
            directed.Add((e.From, e.To));
        }

        // Deterministic node order: strongest first.
        nodeKeys.Sort((a, b) =>
        {
            var c = (outW.GetValueOrDefault(b) + inW.GetValueOrDefault(b)).CompareTo(outW.GetValueOrDefault(a) + inW.GetValueOrDefault(a));
            return c != 0 ? c : string.CompareOrdinal(a, b);
        });
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodeKeys.Count; i++) index[nodeKeys[i]] = i;

        var undirected = new Dictionary<(int, int), double>();
        foreach (var e in edgeList)
        {
            int a = index[e.From], b = index[e.To];
            var key = a < b ? (a, b) : (b, a);
            undirected[key] = undirected.GetValueOrDefault(key) + e.Weight;
        }
        var undirectedList = undirected.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();

        var (membership, modularity) = Louvain.Detect(nodeKeys.Count, undirectedList);

        // Renumber communities by size desc (then first-member order) → ids 1..k.
        var groups = Enumerable.Range(0, nodeKeys.Count).GroupBy(i => membership[i])
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Min())
            .Select((g, i) => (Id: i + 1, Members: g.OrderBy(x => x).ToList()))
            .ToList();
        var communityOf = new int[nodeKeys.Count];
        foreach (var (id, members) in groups)
            foreach (var m in members) communityOf[m] = id;

        // Per-node weight into each community (participation) + community internal/external weight.
        var nodeToComm = new Dictionary<int, double>[nodeKeys.Count];
        for (var i = 0; i < nodeKeys.Count; i++) nodeToComm[i] = new Dictionary<int, double>();
        var internalW = new double[groups.Count + 1];
        var externalW = new double[groups.Count + 1];
        foreach (var (a, b, w) in undirectedList)
        {
            int ca = communityOf[a], cb = communityOf[b];
            nodeToComm[a][cb] = nodeToComm[a].GetValueOrDefault(cb) + w;
            nodeToComm[b][ca] = nodeToComm[b].GetValueOrDefault(ca) + w;
            if (ca == cb) internalW[ca] += w;
            else { externalW[ca] += w; externalW[cb] += w; }
        }

        var replyCounts = Counts(edgeList, e => e.Replies);
        var mentionCounts = Counts(edgeList, e => e.Mentions);

        var nodes = new List<GraphNodeResult>(nodeKeys.Count);
        for (var i = 0; i < nodeKeys.Count; i++)
        {
            var key = nodeKeys[i];
            var total = nodeToComm[i].Values.Sum();
            var participation = total <= 0 ? 0 : 1 - nodeToComm[i].Values.Sum(v => (v / total) * (v / total));
            var partners = neighbours[key];
            nodes.Add(new GraphNodeResult(
                Key: key,
                UserId: key.StartsWith('@') ? null : key,
                Username: names.GetValueOrDefault(key, key),
                IsBroadcaster: broadcasterUserId is { Length: > 0 } && key == broadcasterUserId,
                Messages: sent.GetValueOrDefault(key),
                OutWeight: Math.Round(outW.GetValueOrDefault(key), 3),
                InWeight: Math.Round(inW.GetValueOrDefault(key), 3),
                RepliesSent: replyCounts.Sent.GetValueOrDefault(key),
                RepliesReceived: replyCounts.Received.GetValueOrDefault(key),
                MentionsSent: mentionCounts.Sent.GetValueOrDefault(key),
                MentionsReceived: mentionCounts.Received.GetValueOrDefault(key),
                Partners: partners.Count,
                MutualPartners: partners.Count(p => directed.Contains((key, p)) && directed.Contains((p, key))),
                Community: communityOf[i],
                Participation: Math.Round(participation, 3)));
        }

        var communities = groups.Select(g => new GraphCommunityResult(
            g.Id,
            g.Members.Select(i => nodeKeys[i]).ToList(),
            Math.Round(internalW[g.Id], 3),
            internalW[g.Id] + externalW[g.Id] <= 0 ? 0 : Math.Round(internalW[g.Id] / (internalW[g.Id] + externalW[g.Id]), 3)))
            .ToList();

        var n = nodeKeys.Count;
        return new InteractionGraphResult(
            Messages: messages.Count,
            Chatters: sent.Count,
            Nodes: nodes,
            Edges: edgeList.OrderByDescending(e => e.Weight).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList(),
            Communities: communities,
            Modularity: Math.Round(modularity, 4),
            Reciprocity: edgeList.Count == 0 ? 0 : Math.Round(edgeList.Count(e => directed.Contains((e.To, e.From))) / (double)edgeList.Count, 3),
            Density: n < 2 ? 0 : Math.Round(undirected.Count / (n * (n - 1) / 2.0), 4));
    }

    private static (Dictionary<string, int> Sent, Dictionary<string, int> Received) Counts(
        IEnumerable<GraphEdgeResult> edges, Func<GraphEdgeResult, int> pick)
    {
        var s = new Dictionary<string, int>(StringComparer.Ordinal);
        var r = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            var c = pick(e);
            if (c == 0) continue;
            s[e.From] = s.GetValueOrDefault(e.From) + c;
            r[e.To] = r.GetValueOrDefault(e.To) + c;
        }
        return (s, r);
    }

    private sealed class EdgeAcc
    {
        public int Replies;
        public int Mentions;
        public int Gifts;
        public double Proximity;
    }
}

/// <summary>
/// Louvain community detection (Blondel et al. 2008) on an undirected weighted graph: greedy
/// local moving to maximise modularity, then aggregate communities into super-nodes and repeat.
/// Deterministic for a given node order.
/// </summary>
public static class Louvain
{
    /// <returns>Community index per node (arbitrary ids) and the modularity of that partition.</returns>
    public static (int[] Membership, double Modularity) Detect(
        int nodeCount, IReadOnlyList<(int A, int B, double W)> edges, int maxLevels = 10, int maxPasses = 50)
    {
        var membership = Enumerable.Range(0, nodeCount).ToArray();
        if (nodeCount == 0) return (membership, 0);

        var original = BuildAdjacency(nodeCount, edges);
        var m2 = original.Sum(row => row.Values.Sum());
        if (m2 <= 0) return (membership, 0);

        var adj = original;
        for (var level = 0; level < maxLevels; level++)
        {
            var (comm, moved) = LocalMoving(adj, m2, maxPasses);
            if (!moved) break;

            var renumber = new Dictionary<int, int>();
            foreach (var c in comm)
                if (!renumber.ContainsKey(c)) renumber[c] = renumber.Count;

            for (var i = 0; i < membership.Length; i++)
                membership[i] = renumber[comm[membership[i]]];

            if (renumber.Count == adj.Count) break;

            var next = new List<Dictionary<int, double>>(renumber.Count);
            for (var c = 0; c < renumber.Count; c++) next.Add(new Dictionary<int, double>());
            for (var i = 0; i < adj.Count; i++)
            {
                var ci = renumber[comm[i]];
                foreach (var (j, w) in adj[i])
                {
                    var cj = renumber[comm[j]];
                    next[ci][cj] = next[ci].GetValueOrDefault(cj) + w;
                }
            }
            adj = next;
        }

        return (membership, Modularity(original, membership, m2));
    }

    private static List<Dictionary<int, double>> BuildAdjacency(int n, IReadOnlyList<(int A, int B, double W)> edges)
    {
        var adj = new List<Dictionary<int, double>>(n);
        for (var i = 0; i < n; i++) adj.Add(new Dictionary<int, double>());
        foreach (var (a, b, w) in edges)
        {
            if (w <= 0) continue;
            adj[a][b] = adj[a].GetValueOrDefault(b) + w;
            if (a != b) adj[b][a] = adj[b].GetValueOrDefault(a) + w;
        }
        return adj;
    }

    private static (int[] Comm, bool Moved) LocalMoving(List<Dictionary<int, double>> adj, double m2, int maxPasses)
    {
        var n = adj.Count;
        var comm = Enumerable.Range(0, n).ToArray();
        var k = adj.Select(row => row.Values.Sum()).ToArray();
        var tot = (double[])k.Clone();
        var toComm = new Dictionary<int, double>();
        var anyMove = false;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            var moved = false;
            for (var i = 0; i < n; i++)
            {
                var ci = comm[i];
                toComm.Clear();
                foreach (var (j, w) in adj[i])
                    if (j != i) toComm[comm[j]] = toComm.GetValueOrDefault(comm[j]) + w;

                tot[ci] -= k[i];
                var best = ci;
                var bestGain = toComm.GetValueOrDefault(ci) - tot[ci] * k[i] / m2;
                foreach (var (c, w) in toComm)
                {
                    var gain = w - tot[c] * k[i] / m2;
                    if (gain > bestGain + 1e-12) { best = c; bestGain = gain; }
                }
                tot[best] += k[i];
                if (best != ci) { comm[i] = best; moved = true; anyMove = true; }
            }
            if (!moved) break;
        }
        return (comm, anyMove);
    }

    private static double Modularity(List<Dictionary<int, double>> adj, int[] membership, double m2)
    {
        var inside = new Dictionary<int, double>();
        var tot = new Dictionary<int, double>();
        for (var i = 0; i < adj.Count; i++)
        {
            var ci = membership[i];
            foreach (var (j, w) in adj[i])
            {
                tot[ci] = tot.GetValueOrDefault(ci) + w;
                if (membership[j] == ci) inside[ci] = inside.GetValueOrDefault(ci) + w;
            }
        }
        return tot.Keys.Sum(c => inside.GetValueOrDefault(c) / m2 - Math.Pow(tot[c] / m2, 2));
    }
}
