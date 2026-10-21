using System.Text.Json;

namespace ChatPlatform.Core.Events;

public sealed record PlatformEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Type { get; init; }
    public required string Source { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public JsonElement Payload { get; init; }
}
