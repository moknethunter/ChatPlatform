using ChatPlatform.Core.Events;

namespace ChatPlatform.Core.Abstractions;

public interface IEventBus
{
    ValueTask PublishAsync(PlatformEvent evt, CancellationToken ct = default);
    IAsyncEnumerable<PlatformEvent> SubscribeAsync(CancellationToken ct);
}
