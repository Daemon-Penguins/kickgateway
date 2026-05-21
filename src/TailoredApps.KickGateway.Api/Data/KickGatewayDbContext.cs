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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KickClientApp>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ClientId).IsUnique();
            b.HasIndex(x => x.Name).IsUnique();
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

        // MassTransit transactional outbox + inbox tables — registered here so EF migrations create them.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        base.OnModelCreating(modelBuilder);
    }
}
