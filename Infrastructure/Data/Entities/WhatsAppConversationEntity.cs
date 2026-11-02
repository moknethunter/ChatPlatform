namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class WhatsAppConversationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = TenantEntity.DefaultTenantId;
    public string PhoneNumberId { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string? ContactName { get; set; }
    public DateTime? LastInboundAtUtc { get; set; }
    public DateTime? LastOutboundAtUtc { get; set; }
    public DateTime? WindowExpiresAtUtc { get; set; }
    public string Status { get; set; } = "open";
    public string? AssignedToUserId { get; set; }
    public int UnreadCount { get; set; }
    public string? Tags { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<WhatsAppMessageEntity> Messages { get; set; } = new List<WhatsAppMessageEntity>();
}
