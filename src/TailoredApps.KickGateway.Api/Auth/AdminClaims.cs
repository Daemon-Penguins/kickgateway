using System.Security.Claims;
using TailoredApps.KickGateway.Api.Data;

namespace TailoredApps.KickGateway.Api.Auth;

/// <summary>
/// Canonical claim type names + helpers for reading the gateway-specific bits
/// off a <see cref="ClaimsPrincipal"/>. We don't use the framework Role claim —
/// per-client roles need both a role and a client-id together, which doesn't
/// fit a single string. Encoded as <c>client_role</c> = <c>"{clientId}:{role}"</c>.
/// </summary>
public static class AdminClaims
{
    public const string AuthenticationScheme = "AdminCookie";

    /// <summary>Internal <see cref="AdminUser.Id"/> (Guid string).</summary>
    public const string AdminUserId = "admin_user_id";

    /// <summary>"SuperAdmin" — present only when the user has the global role.</summary>
    public const string GlobalRole = "global_role";

    /// <summary>"{kickClientAppId}:{AdminRole}" — one claim per per-client grant.</summary>
    public const string ClientRole = "client_role";

    public static bool IsSuperAdmin(this ClaimsPrincipal user)
        => user.HasClaim(c => c.Type == GlobalRole && c.Value == nameof(AdminRole.SuperAdmin));

    public static IEnumerable<(Guid ClientAppId, AdminRole Role)> ClientRoles(this ClaimsPrincipal user)
    {
        foreach (var c in user.FindAll(ClientRole))
        {
            var parts = c.Value.Split(':', 2);
            if (parts.Length != 2) continue;
            if (!Guid.TryParse(parts[0], out var clientId)) continue;
            if (!Enum.TryParse<AdminRole>(parts[1], out var role)) continue;
            yield return (clientId, role);
        }
    }

    /// <summary>
    /// True when the user has at least <paramref name="minimum"/> level on the
    /// given client (or is SuperAdmin). Role precedence: ClientAdmin &gt;
    /// ClientOperator &gt; ClientViewer.
    /// </summary>
    public static bool HasClientRoleAtLeast(this ClaimsPrincipal user, Guid clientAppId, AdminRole minimum)
    {
        if (user.IsSuperAdmin()) return true;
        var rank = Rank(minimum);
        foreach (var (id, role) in user.ClientRoles())
        {
            if (id == clientAppId && Rank(role) >= rank) return true;
        }
        return false;
    }

    /// <summary>The set of client-app ids the user can view at all (any per-client role).</summary>
    public static HashSet<Guid> AccessibleClientAppIds(this ClaimsPrincipal user)
        => user.ClientRoles().Select(x => x.ClientAppId).ToHashSet();

    // Higher number = more privileged. SuperAdmin handled separately.
    private static int Rank(AdminRole r) => r switch
    {
        AdminRole.ClientAdmin => 3,
        AdminRole.ClientOperator => 2,
        AdminRole.ClientViewer => 1,
        AdminRole.SuperAdmin => int.MaxValue,
        _ => 0,
    };
}
