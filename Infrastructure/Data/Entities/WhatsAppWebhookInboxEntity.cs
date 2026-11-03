using System.ComponentModel.DataAnnotations;

namespace ChatPlatform.Infrastructure.Data.Entities;

/// <summary>Durable, raw Meta webhook delivery. Raw payload is retained for replay/audit and can contain personal data.</summary>
public sealed class WhatsAppWebhookInboxEntity
{
    public long Id { get; set; }
    [MaxLength(64)] public string PayloadHash { get; set; } = string.Empty;
    [MaxLength(128)] public string? MessageId { get; set; }
    [MaxLength(64)] public string Status { get; set; } = "Pending";
    public string RawBody { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
