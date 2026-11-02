namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class WhatsAppOutboxEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = TenantEntity.DefaultTenantId;
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public string IdempotencyKey { get; set; } = "";
    public Guid? MessageId { get; set; }
    public string PhoneNumberId { get; set; } = "";
    public string RecipientPhone { get; set; } = "";
    public string MessageType { get; set; } = "text";
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 10;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ScheduledForUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? MetaMessageId { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
