using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;

namespace ChatPlatform.Infrastructure.Messaging;

public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<PlatformEvent>> _subscribers = new();

    public async ValueTask PublishAsync(PlatformEvent evt, CancellationToken ct = default)
    {
        foreach (var ch in _subscribers.Values)
        {
            try { await ch.Writer.WriteAsync(evt, ct); }
            catch (ChannelClosedException) { }
        }
    }

    public async IAsyncEnumerable<PlatformEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<PlatformEvent>(new UnboundedChannelOptions { SingleReader = true });
        _subscribers[id] = ch;
        try
        {
            await foreach (var evt in ch.Reader.ReadAllAsync(ct))
                yield return evt;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            ch.Writer.TryComplete();
        }
    }
}
