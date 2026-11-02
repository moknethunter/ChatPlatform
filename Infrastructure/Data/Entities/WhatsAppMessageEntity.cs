namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class WhatsAppMessageEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = TenantEntity.DefaultTenantId;
    public Guid ConversationId { get; set; }
    public WhatsAppConversationEntity? Conversation { get; set; }
    public string? Wamid { get; set; }
    public string Direction { get; set; } = "Inbound";
    public string MessageType { get; set; } = "text";
    public string ContentJson { get; set; } = "{}";
    public string? TextPreview { get; set; }
    public string? MediaMimeType { get; set; }
    public string? MediaUrl { get; set; }
    public string Status { get; set; } = "Received";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
