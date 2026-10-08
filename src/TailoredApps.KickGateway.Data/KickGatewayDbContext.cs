using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace TailoredApps.KickGateway.Api.Data;

public class KickGatewayDbContext : DbContext
{
    public KickGatewayDbContext(DbContextOptions<KickGatewayDbContext> options) : base(options) { }

    public DbSet<KickClientApp> ClientApps => Set<KickClientApp>();
    public DbSet<KickBroadcasterAccount> Broadcasters => Set<KickBroadcasterAccount>();
    public DbSet<RealtimeChannel> RealtimeChannels => Set<RealtimeChannel>();
    public DbSet<KickEventSubscription> EventSubscriptions => Set<KickEventSubscription>();
    public DbSet<PkceStateEntry> PkceStates => Set<PkceStateEntry>();
    public DbSet<ReceivedWebhook> ReceivedWebhooks => Set<ReceivedWebhook>();
    public DbSet<ReceivedRealtimeEvent> ReceivedRealtimeEvents => Set<ReceivedRealtimeEvent>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AdminUserRole> AdminUserRoles => Set<AdminUserRole>();

    // Chat-analytics read model (projected from ReceivedWebhooks + ReceivedRealtimeEvents by the Api).
    public DbSet<ChatMessageRecord> ChatMessages => Set<ChatMessageRecord>();
    public DbSet<ChatMention> ChatMentions => Set<ChatMention>();
    public DbSet<ChatterEvent> ChatterEvents => Set<ChatterEvent>();
    public DbSet<AnalyticsCheckpoint> AnalyticsCheckpoints => Set<AnalyticsCheckpoint>();

    // Deterministic Guids for the bootstrap SuperAdmin so the EF migration is
    // reproducible. The username on the seeded row is a placeholder
    // ("superadmin"); the real username is set at startup from the
    // SEED_SUPERADMIN_USERNAME env var (see Program.cs). Existing prod rows
    // keep whatever username they already have.
    public static readonly Guid SeedSuperAdminId = new("11111111-1111-1111-1111-111111111111");
    public const string SeedSuperAdminPlaceholderUsername = "superadmin";
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
            // Existing broadcasters opt into the public OBS clips page by default,
            // with the original behaviour: newest-first, two lead-in clips, shuffle.
            b.Property(x => x.ClipsDisplayEnabled).HasDefaultValue(true);
            b.Property(x => x.VideoCaptureEnabled).HasDefaultValue(true);
            b.Property(x => x.ClipsSortMode).HasDefaultValue(ClipsSortMode.Latest);
            b.Property(x => x.ClipsTimeWindow).HasDefaultValue(ClipsTimeWindow.All);
            b.Property(x => x.ClipsLeadInCount).HasDefaultValue(2);
            b.Property(x => x.ClipsShuffle).HasDefaultValue(true);
        });

        modelBuilder.Entity<RealtimeChannel>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Slug).IsUnique();
            b.Property(x => x.IsEnabled).HasDefaultValue(true);
            b.Property(x => x.CaptureChat).HasDefaultValue(true);
            b.Property(x => x.CaptureChannel).HasDefaultValue(true);
            b.Property(x => x.VideoCaptureEnabled).HasDefaultValue(false);
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

        modelBuilder.Entity<ReceivedRealtimeEvent>(b =>
        {
            b.HasKey(x => x.DedupeKey);
            b.HasIndex(x => x.ReceivedAt);
            b.HasIndex(x => x.Slug);
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
                KickUserId = "",                                  // resolved on first login
                Username = SeedSuperAdminPlaceholderUsername,     // overridden at startup from env var
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

        modelBuilder.Entity<ChatMessageRecord>(b =>
        {
            b.HasKey(x => x.MessageId);
            // Channel windows (overview, graph, transcript).
            b.HasIndex(x => new { x.ChannelSlug, x.CreatedAt });
            // Per-chatter aggregates inside a channel (first seen, new vs returning).
            b.HasIndex(x => new { x.ChannelSlug, x.SenderUserId, x.CreatedAt });
            // Per-chatter profile across channels (+ "latest username of id" covered).
            b.HasIndex(x => new { x.SenderUserId, x.CreatedAt }).IncludeProperties(x => x.SenderUsername);
            // Replies received.
            b.HasIndex(x => new { x.ReplyToUserId, x.CreatedAt });
            b.HasIndex(x => x.ReplyToMessageId);
            // Username → user id resolution + search (covered, latest-first per name).
            b.HasIndex(x => new { x.SenderUsername, x.CreatedAt }).IncludeProperties(x => x.SenderUserId);
            b.HasMany(x => x.Mentions)
                .WithOne(x => x.Message)
                .HasForeignKey(x => x.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMention>(b =>
        {
            b.HasKey(x => new { x.MessageId, x.MentionedUsername });
            b.HasIndex(x => x.MentionedUsername);
        });

        modelBuilder.Entity<ChatterEvent>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.UserId, x.OccurredAt });
            b.HasIndex(x => new { x.CounterpartUserId, x.OccurredAt });
            b.HasIndex(x => new { x.ChannelSlug, x.OccurredAt });
            // "Was this message deleted?" lookups for transcripts.
            b.HasIndex(x => x.RefId);
        });

        modelBuilder.Entity<AnalyticsCheckpoint>(b => b.HasKey(x => x.Name));

        // MassTransit transactional outbox + inbox tables — registered here so EF migrations create them.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        base.OnModelCreating(modelBuilder);
    }
}
