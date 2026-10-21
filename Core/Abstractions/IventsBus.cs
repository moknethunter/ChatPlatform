using ChatPlatform.Core.Events;

namespace ChatPlatform.Core.Abstractions;

public interface IEventBus
{
    ValueTask PublishAsync(PlatformEvent evt, CancellationToken ct = default);
    // كل مشترك يحصل على قناة مستقلة (Fan-out) لضمان عدم تداخل التكاملات
    IAsyncEnumerable<PlatformEvent> SubscribeAsync(CancellationToken ct);
}
