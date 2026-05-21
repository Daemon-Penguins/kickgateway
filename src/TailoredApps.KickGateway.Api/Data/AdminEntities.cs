using System.ComponentModel.DataAnnotations;

namespace TailoredApps.KickGateway.Api.Data;

/// <summary>
/// Role catalogue. Mix of one global role (SuperAdmin) and three per-client
/// roles. The per-client roles MUST have a <see cref="AdminUserRole.KickClientAppId"/>
/// set; SuperAdmin MUST have it null. Enforced in <see cref="KickGatewayDbContext"/>.
/// </summary>
public enum AdminRole
{
    /// <summary>Global — can do anything, including managing other admins.</summary>
    SuperAdmin = 1,

    /// <summary>Per-client — full CRUD over the client's broadcasters, OAuth, subscriptions.</summary>
    ClientAdmin = 2,

    /// <summary>Per-client — read everything for the client + can trigger Enroll/Refresh.</summary>
    ClientOperator = 3,

    /// <summary>Per-client — read-only; cannot trigger actions or mutate state.</summary>
    ClientViewer = 4,
}

/// <summary>
/// One row per admin person, keyed by their Kick numeric user id. Created
/// automatically on first successful admin SSO login when an entry exists
/// pre-seeded by Username (bootstrap path), or by a SuperAdmin from the
/// Admins management page.
/// </summary>
public class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Numeric Kick user id (stored as string for consistency with the rest of
    /// the data model). Empty string at bootstrap time means "not resolved yet";
    /// the OAuth callback fills it on first successful login.
    /// </summary>
    [MaxLength(64)]
    public string KickUserId { get; set; } = "";

    [MaxLength(120)]
    public string Username { get; set; } = "";

    [MaxLength(256)]
    public string? Email { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    public List<AdminUserRole> Roles { get; set; } = new();
}

/// <summary>
/// Role grant for a user. SuperAdmin → <see cref="KickClientAppId"/> is null.
/// Anything else → <see cref="KickClientAppId"/> is required.
/// </summary>
public class AdminUserRole
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AdminUserId { get; set; }
    public AdminUser? AdminUser { get; set; }

    public AdminRole Role { get; set; }

    public Guid? KickClientAppId { get; set; }
    public KickClientApp? KickClientApp { get; set; }

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
}
