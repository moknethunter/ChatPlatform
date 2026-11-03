using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Data;

public class PlatformDbContext : DbContext, IDataProtectionKeyContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }

    public DbSet<ConnectorEntity> Connectors => Set<ConnectorEntity>();
    public DbSet<WhatsAppTenantEntity> WhatsAppTenants => Set<WhatsAppTenantEntity>();
    public DbSet<WhatsAppWebhookInboxEntity> WhatsAppWebhookInbox => Set<WhatsAppWebhookInboxEntity>();
    public DbSet<WhatsAppOutboxMessageEntity> WhatsAppOutbox => Set<WhatsAppOutboxMessageEntity>();
    public DbSet<WhatsAppDeadLetterEntity> WhatsAppDeadLetters => Set<WhatsAppDeadLetterEntity>();

    // Required for Data Protection persistence
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<WhatsAppWebhookInboxEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.PayloadHash).IsUnique();
            entity.HasIndex(x => x.MessageId);
            entity.HasIndex(x => new { x.Status, x.NextAttemptAt, x.ReceivedAt });
            entity.Property(x => x.RawBody).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<WhatsAppOutboxMessageEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CorrelationId).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextRetryAt, x.CreatedAt });
            entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Channel).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
        });

        modelBuilder.Entity<WhatsAppDeadLetterEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Channel, x.MessageKey }).IsUnique();
            entity.Property(x => x.MessageKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Channel).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.Error).IsRequired();
        });
    }
}
