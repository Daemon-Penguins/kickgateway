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
}
