namespace TailoredApps.Integrations.Kick.Sidecar;

/// <summary>
/// Fetches a (Cloudflare-protected) Kick URL through the browser-TLS "clips-fetcher"
/// sidecar and returns the upstream body, or null on any failure. Shared by the
/// clips and channel-stats clients so the sidecar URL/secret logic lives in one place.
/// </summary>
public interface IKickSidecarFetcher
{
    Task<string?> FetchAsync(string kickUrl, CancellationToken ct = default);
}
