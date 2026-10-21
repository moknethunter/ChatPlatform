using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;

namespace ChatPlatform.Infrastructure.Adapters;

public sealed class ConsoleAdapter : IIntegrationAdapter
{
    public string Name => "ConsoleLogger";

    public Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        Console.WriteLine($"[ADAPTER] Event: {evt.Type} from {evt.Source} | Payload: {evt.Payload}");
        return Task.CompletedTask;
    }
}
