using System.Text.Json;

namespace TailoredApps.Integrations.Kick.Internal;

internal static class JsonExtensions
{
    public static string ReadAsString(this JsonElement parent, string prop, string fallback = "")
    {
        if (!parent.TryGetProperty(prop, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? fallback,
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
            _ => fallback
        };
    }

    public static int ReadAsInt(this JsonElement parent, string prop, int fallback = 0)
    {
        if (!parent.TryGetProperty(prop, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out var i) ? i : (el.TryGetDouble(out var d) ? (int)d : fallback),
            JsonValueKind.String => int.TryParse(el.GetString(), out var s) ? s : fallback,
            _ => fallback
        };
    }

    public static bool ReadAsBool(this JsonElement parent, string prop, bool fallback = false)
    {
        if (!parent.TryGetProperty(prop, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : fallback,
            _ => fallback
        };
    }

    public static DateTime ReadAsDateTime(this JsonElement parent, string prop, DateTime fallback = default)
    {
        if (parent.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String && el.TryGetDateTime(out var dt))
            return dt.ToUniversalTime();
        return fallback;
    }
}
