namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class WhatsAppWebhookInboxEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = TenantEntity.DefaultTenantId;
    public string EventKey { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 10;
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LeaseUntilUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public string? LastError { get; set; }
}
