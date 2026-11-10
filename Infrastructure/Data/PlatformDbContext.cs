using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Data;

public class PlatformDbContext : DbContext, IDataProtectionKeyContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }

    public DbSet<ConnectorEntity> Connectors => Set<ConnectorEntity>();
    public DbSet<WhatsAppTenantEntity> WhatsAppTenants => Set<WhatsAppTenantEntity>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<WhatsAppWebhookInboxEntity> WhatsAppWebhookInbox => Set<WhatsAppWebhookInboxEntity>();
    public DbSet<WhatsAppStatusInboxEntity> WhatsAppStatusInbox => Set<WhatsAppStatusInboxEntity>();
    public DbSet<WhatsAppConversationEntity> WhatsAppConversations => Set<WhatsAppConversationEntity>();
    public DbSet<WhatsAppMessageEntity> WhatsAppMessages => Set<WhatsAppMessageEntity>();
    public DbSet<WhatsAppOutboxEntity> WhatsAppOutbox => Set<WhatsAppOutboxEntity>();
    public DbSet<WhatsAppTemplateEntity> WhatsAppTemplates => Set<WhatsAppTemplateEntity>();
    public DbSet<AutoReplyRuleEntity> AutoReplyRules => Set<AutoReplyRuleEntity>();
    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        mb.Entity<TenantEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).IsRequired();
            e.HasData(new TenantEntity
            {
                Id = TenantEntity.DefaultTenantId,
                Name = "Default Tenant",
                IsActive = true,
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        mb.Entity<WhatsAppTenantEntity>(e =>
        {
            e.HasIndex(x => x.PhoneNumberId).IsUnique();
            e.HasOne(x => x.Tenant)
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<WhatsAppWebhookInboxEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EventKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            e.Property(x => x.EventKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.LeaseOwner).HasMaxLength(200);
        });

        mb.Entity<WhatsAppStatusInboxEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EventKey).IsUnique();
            e.HasIndex(x => new { x.Wamid, x.Processed });
            e.Property(x => x.EventKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.Wamid).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
        });

        mb.Entity<WhatsAppConversationEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PhoneNumberId, x.ContactPhone }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.WindowExpiresAtUtc);
            e.Property(x => x.ContactPhone).HasMaxLength(30).IsRequired();
            e.Property(x => x.PhoneNumberId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
        });

        mb.Entity<WhatsAppMessageEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Wamid).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ConversationId, x.TimestampUtc });
            e.Property(x => x.Wamid).HasMaxLength(200);
            e.Property(x => x.TextPreview).HasMaxLength(500);
            e.HasOne(x => x.Conversation)
                .WithMany(x => x.Messages)
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<WhatsAppOutboxEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            e.HasIndex(x => x.MetaMessageId).IsUnique();
            e.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.PhoneNumberId).HasMaxLength(100).IsRequired();
            e.Property(x => x.RecipientPhone).HasMaxLength(30).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.LeaseOwner).HasMaxLength(200);
            e.Property(x => x.MetaMessageId).HasMaxLength(200);
        });

        mb.Entity<AutoReplyRuleEntity>(e =>
{
    e.HasKey(x => x.Id);
    e.HasIndex(x => new { x.TenantId, x.IsActive, x.Priority });
    e.Property(x => x.Name).HasMaxLength(100).IsRequired();
    e.Property(x => x.MatchType).HasMaxLength(20).IsRequired();
    e.Property(x => x.KeywordPattern).HasMaxLength(500);
    e.Property(x => x.ReplyText).HasMaxLength(2000).IsRequired();
});
    mb.Entity<WhatsAppTemplateEntity>(e =>
{
    e.HasKey(x => x.Id);

    e.Property(x => x.TenantId)
        .HasMaxLength(100)
        .IsRequired();

    e.Property(x => x.WhatsAppTenantId)
        .HasMaxLength(100)
        .IsRequired();

    e.Property(x => x.Name)
        .HasMaxLength(100)
        .IsRequired();

    e.Property(x => x.Category)
        .HasMaxLength(30)
        .IsRequired();

    e.Property(x => x.Language)
        .HasMaxLength(20)
        .IsRequired();

    e.Property(x => x.Body)
        .HasMaxLength(4000)
        .IsRequired();

    e.Property(x => x.Status)
        .HasMaxLength(30)
        .IsRequired();

    e.HasIndex(x => new
    {
        x.TenantId,
        x.WhatsAppTenantId,
        x.Name
    }).IsUnique();

    e.HasIndex(x => new
    {
        x.TenantId,
        x.WhatsAppTenantId,
        x.IsActive
    });

    e.HasOne<WhatsAppTenantEntity>()
        .WithMany()
        .HasForeignKey(x => x.WhatsAppTenantId)
        .OnDelete(DeleteBehavior.Restrict);
});

    }
}
