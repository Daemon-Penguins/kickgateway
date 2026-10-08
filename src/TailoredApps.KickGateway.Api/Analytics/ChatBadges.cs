using System.Text;
using System.Text.Json;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>A chat badge: Kick <c>type</c> (subscriber, moderator, vip, og, founder, sub_gifter, …) + count (months / gifts).</summary>
public sealed record ChatBadge(string Type, int Count);

/// <summary>
/// Compact storage for sender badges (<see cref="Data.ChatMessageRecord.SenderBadges"/>):
/// <c>type:count|type:count</c>. Badges are channel-specific (subscriber months differ per
/// channel), so they are kept per message rather than per user.
/// </summary>
public static class ChatBadges
{
    public static string? Encode(JsonElement identity)
    {
        if (identity.ValueKind != JsonValueKind.Object
            || !identity.TryGetProperty("badges", out var badges)
            || badges.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        foreach (var b in badges.EnumerateArray())
        {
            string type;
            var count = 0;
            if (b.ValueKind == JsonValueKind.String) type = b.GetString() ?? "";
            else if (b.ValueKind == JsonValueKind.Object)
            {
                type = ChatProjectionMapper.Str(b, "type");
                if (type.Length == 0) type = ChatProjectionMapper.Str(b, "text");
                count = ChatProjectionMapper.Int(b, "count");
            }
            else continue;

            type = Sanitize(type);
            if (type.Length == 0) continue;
            if (sb.Length > 0) sb.Append('|');
            sb.Append(type).Append(':').Append(count);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    public static IReadOnlyList<ChatBadge> Parse(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return Array.Empty<ChatBadge>();
        var list = new List<ChatBadge>();
        foreach (var part in encoded.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.LastIndexOf(':');
            if (idx <= 0) { list.Add(new ChatBadge(part, 0)); continue; }
            list.Add(new ChatBadge(part[..idx], int.TryParse(part[(idx + 1)..], out var n) ? n : 0));
        }
        return list;
    }

    private static string Sanitize(string s) =>
        new(s.Trim().ToLowerInvariant().Replace(' ', '_').Where(ch => ch != '|' && ch != ':').ToArray());
}
