namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class WhatsAppStatusInboxEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = TenantEntity.DefaultTenantId;
    public string EventKey { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string Wamid { get; set; } = "";
    public string Status { get; set; } = "";
    public long StatusTimestamp { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public bool Processed { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
}
