using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.Integrations.Kick.Channels;

/// <summary>
/// Reads a Kick channel's live info/stats (viewer count, live state, category, …)
/// from the Cloudflare-protected website API via the sidecar. The official
/// <c>public/v1</c> API does not expose viewer counts, hence the sidecar.
/// </summary>
public interface IKickChannelClient
{
    /// <summary>Fetch channel info by slug. Null if the channel is unknown or the fetch failed.</summary>
    Task<KickChannelInfo?> GetChannelAsync(string slug, CancellationToken ct = default);
}
