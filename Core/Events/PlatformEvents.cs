using System.Text.Json;

namespace ChatPlatform.Core.Events;

public sealed record PlatformEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Type { get; init; } // مثل: message.received
    public required string Source { get; init; } // من أين جاء الحدث؟
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public JsonElement Payload { get; init; } // البيانات نفسها
}
