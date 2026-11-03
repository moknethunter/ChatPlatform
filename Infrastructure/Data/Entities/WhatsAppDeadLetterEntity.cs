using System.ComponentModel.DataAnnotations;

namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class WhatsAppDeadLetterEntity
{
    public long Id { get; set; }
    [MaxLength(128)] public string MessageKey { get; set; } = string.Empty;
    [MaxLength(64)] public string Channel { get; set; } = "WhatsApp";
    public string Payload { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset DeadLetteredAt { get; set; } = DateTimeOffset.UtcNow;
}
