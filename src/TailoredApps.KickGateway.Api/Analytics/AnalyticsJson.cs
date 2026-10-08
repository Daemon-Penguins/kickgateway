using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TailoredApps.KickGateway.Api.Analytics;

/// <summary>JSON shape of the analytics endpoints: camelCase, nulls omitted, every DateTime written as UTC (<c>…Z</c>).</summary>
public static class AnalyticsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new UtcDateTimeConverter() },
    };

    /// <summary>SQL Server hands DateTimes back as <see cref="DateTimeKind.Unspecified"/>; everything stored here is UTC, so say so.</summary>
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTime().ToUniversalTime();

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}

/// <summary>
/// Parses the <c>from</c>/<c>to</c> query parameters: ISO-8601 (<c>2026-09-01</c>,
/// <c>2026-09-01T18:00:00Z</c>; no offset = UTC), relative look-backs (<c>30m</c>, <c>12h</c>,
/// <c>7d</c>, <c>4w</c>), or <c>all</c> for an unbounded start.
/// </summary>
public static class AnalyticsTime
{
    public static bool TryParse(string? value, DateTime nowUtc, out DateTime? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var v = value.Trim();

        if (v.Length >= 2 && char.IsAsciiLetter(v[^1]) && int.TryParse(v[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            TimeSpan? span = char.ToLowerInvariant(v[^1]) switch
            {
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                'w' => TimeSpan.FromDays(7 * n),
                _ => null,
            };
            if (span is null) return false;
            result = nowUtc - span.Value;
            return true;
        }

        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            result = dto.UtcDateTime;
            return true;
        }
        return false;
    }

    /// <param name="defaultDays">Look-back when <paramref name="from"/> is omitted; null = unbounded.</param>
    public static bool TryResolveWindow(string? from, string? to, int? defaultDays, DateTime nowUtc,
        out AnalyticsWindow window, out string? error)
    {
        window = new AnalyticsWindow(null, nowUtc);
        error = null;

        if (!TryParse(to, nowUtc, out var toValue))
        {
            error = $"invalid 'to': {to} (use ISO-8601 like 2026-09-01T18:00:00Z or a look-back like 12h/7d)";
            return false;
        }
        var end = toValue ?? nowUtc;

        DateTime? start;
        if (string.Equals(from?.Trim(), "all", StringComparison.OrdinalIgnoreCase)) start = null;
        else if (string.IsNullOrWhiteSpace(from)) start = defaultDays is { } d ? end.AddDays(-d) : null;
        else if (!TryParse(from, nowUtc, out start))
        {
            error = $"invalid 'from': {from} (use ISO-8601, a look-back like 7d/12h, or 'all')";
            return false;
        }

        if (start is not null && start >= end)
        {
            error = "'from' must be before 'to'";
            return false;
        }
        window = new AnalyticsWindow(start, end);
        return true;
    }
}
