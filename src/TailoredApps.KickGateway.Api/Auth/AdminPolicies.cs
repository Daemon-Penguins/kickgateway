using Microsoft.AspNetCore.Authorization;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Auth;

/// <summary>
/// Named policies. The per-client policies are intentionally NOT parametric in
/// the route — instead, endpoints take the client-app id from the route and
/// call <c>HttpContext.User.HasClientRoleAtLeast(id, role)</c> directly. This
/// keeps the policy set small (4) instead of multiplying per-client-id.
/// </summary>
public static class AdminPolicies
{
    public const string SuperAdminOnly = nameof(SuperAdminOnly);
    public const string AnyAuthenticatedAdmin = nameof(AnyAuthenticatedAdmin);

    public static AuthorizationOptions AddKickGatewayPolicies(this AuthorizationOptions o)
    {
        o.AddPolicy(SuperAdminOnly, p => p
            .RequireAuthenticatedUser()
            .RequireClaim(AdminClaims.GlobalRole, nameof(AdminRole.SuperAdmin)));

        // Granular per-client checks happen in endpoint code; this policy just
        // gates "is a logged-in admin at all" — useful for the admin UI shell.
        o.AddPolicy(AnyAuthenticatedAdmin, p => p
            .RequireAuthenticatedUser());

        return o;
    }
}
