using System.ComponentModel.DataAnnotations;

namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class WhatsAppOutboxMessageEntity
{
    public long Id { get; set; }
    [MaxLength(128)] public string CorrelationId { get; set; } = string.Empty;
    [MaxLength(64)] public string Status { get; set; } = "Created";
    [MaxLength(64)] public string Channel { get; set; } = "WhatsApp";
    public string Payload { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 8;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextRetryAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public string? LastError { get; set; }
    [MaxLength(128)] public string? ExternalMessageId { get; set; }
}
