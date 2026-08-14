using System.Security.Claims;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Auth;

/// <summary>
/// Builds the admin cookie principal from an <see cref="AdminUser"/>. Shared by the
/// Kick OAuth callback (<c>AdminAuthEndpoints</c>) and the dev-only local sign-in
/// (<c>DevAuthEndpoints</c>) so both mint byte-identical claims — same NameIdentifier,
/// same global/per-client role claims. The user's <see cref="AdminUser.Roles"/> must
/// be loaded before calling.
/// </summary>
public static class AdminPrincipalFactory
{
    public static ClaimsPrincipal Build(AdminUser admin, string? fallbackNameId = null)
    {
        // Bootstrap rows have an empty KickUserId (the real OAuth login fills it later),
        // so allow a caller-supplied fallback for the NameIdentifier claim without
        // touching the persisted row. Real OAuth logins always pass a resolved id.
        var nameId = string.IsNullOrEmpty(admin.KickUserId)
            ? (fallbackNameId ?? admin.Id.ToString())
            : admin.KickUserId;

        var identity = new ClaimsIdentity(AdminClaims.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, nameId));
        identity.AddClaim(new Claim(ClaimTypes.Name, admin.Username));
        identity.AddClaim(new Claim(AdminClaims.AdminUserId, admin.Id.ToString()));
        foreach (var r in admin.Roles)
        {
            if (r.Role == AdminRole.SuperAdmin)
                identity.AddClaim(new Claim(AdminClaims.GlobalRole, nameof(AdminRole.SuperAdmin)));
            else if (r.KickClientAppId is { } cid)
                identity.AddClaim(new Claim(AdminClaims.ClientRole, $"{cid}:{r.Role}"));
        }

        return new ClaimsPrincipal(identity);
    }
}
