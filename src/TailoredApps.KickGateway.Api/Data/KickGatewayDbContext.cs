using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace TailoredApps.KickGateway.Api.Data;

public class KickGatewayDbContext : DbContext
{
    public KickGatewayDbContext(DbContextOptions<KickGatewayDbContext> options) : base(options) { }

    public DbSet<KickClientApp> ClientApps => Set<KickClientApp>();
    public DbSet<KickBroadcasterAccount> Broadcasters => Set<KickBroadcasterAccount>();
    public DbSet<KickEventSubscription> EventSubscriptions => Set<KickEventSubscription>();
    public DbSet<PkceStateEntry> PkceStates => Set<PkceStateEntry>();
    public DbSet<ReceivedWebhook> ReceivedWebhooks => Set<ReceivedWebhook>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AdminUserRole> AdminUserRoles => Set<AdminUserRole>();

    // Deterministic Guids for the bootstrap SuperAdmin so the EF migration is reproducible.
    private static readonly Guid SeedSuperAdminId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SeedSuperAdminRoleId = new("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime SeedTimestamp = new(2026, 5, 21, 0, 0, 0, DateTimeKind.Utc);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KickClientApp>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ClientId).IsUnique();
            b.HasIndex(x => x.Name).IsUnique();
            // Filtered unique index — only one client app can be the admin SSO client.
            b.HasIndex(x => x.IsAdminLoginClient)
                .IsUnique()
                .HasFilter("[IsAdminLoginClient] = 1");
        });

        modelBuilder.Entity<KickBroadcasterAccount>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasOne(x => x.KickClientApp)
                .WithMany(x => x.Accounts)
                .HasForeignKey(x => x.KickClientAppId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.KickClientAppId, x.KickUserId }).IsUnique();
        });

        modelBuilder.Entity<KickEventSubscription>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasOne(x => x.Broadcaster)
                .WithMany(x => x.Subscriptions)
                .HasForeignKey(x => x.KickBroadcasterAccountId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.KickBroadcasterAccountId, x.EventType, x.Version }).IsUnique();
        });

        modelBuilder.Entity<PkceStateEntry>(b =>
        {
            b.HasKey(x => x.State);
            b.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<ReceivedWebhook>(b =>
        {
            b.HasKey(x => x.MessageId);
            b.HasIndex(x => x.ReceivedAt);
            b.HasIndex(x => x.BroadcasterAccountId);
        });

        modelBuilder.Entity<AdminUser>(b =>
        {
            b.HasKey(x => x.Id);
            // Filtered unique on KickUserId — bootstrap rows have "" until first login.
            b.HasIndex(x => x.KickUserId)
                .IsUnique()
                .HasFilter("[KickUserId] <> ''");
            b.HasIndex(x => x.Username);
            b.HasData(new AdminUser
            {
                Id = SeedSuperAdminId,
                KickUserId = "",                 // resolved on first login
                Username = "nieprzecietny_kowalski",
                IsEnabled = true,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
            });
        });

        modelBuilder.Entity<AdminUserRole>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasOne(x => x.AdminUser)
                .WithMany(x => x.Roles)
                .HasForeignKey(x => x.AdminUserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.KickClientApp)
                .WithMany()
                .HasForeignKey(x => x.KickClientAppId)
                .OnDelete(DeleteBehavior.Cascade);
            // Per-(user, client, role) uniqueness. KickClientAppId NULL groups
            // SuperAdmin rows; SQL Server treats NULL distinctly in unique
            // indexes by default, so two SuperAdmin grants for the same user
            // would be allowed without the filtered index below.
            b.HasIndex(x => new { x.AdminUserId, x.KickClientAppId, x.Role }).IsUnique();
            b.HasIndex(x => new { x.AdminUserId, x.Role })
                .IsUnique()
                .HasFilter("[KickClientAppId] IS NULL");
            // Bootstrap: SuperAdmin grant for the seeded user.
            b.HasData(new AdminUserRole
            {
                Id = SeedSuperAdminRoleId,
                AdminUserId = SeedSuperAdminId,
                Role = AdminRole.SuperAdmin,
                KickClientAppId = null,
                GrantedAt = SeedTimestamp,
            });
        });

        // MassTransit transactional outbox + inbox tables — registered here so EF migrations create them.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        base.OnModelCreating(modelBuilder);
    }
}
