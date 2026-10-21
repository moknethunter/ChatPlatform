using ChatPlatform.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Engine;

public sealed class CoreEngine : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IEnumerable<IIntegrationAdapter> _adapters;
    private readonly ILogger<CoreEngine> _log;

    public CoreEngine(IEventBus bus, IEnumerable<IIntegrationAdapter> adapters, ILogger<CoreEngine> log)
    {
        _bus = bus;
        _adapters = adapters;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Core Engine Started. Loaded {Count} integration adapters.", _adapters.Count());

        await foreach (var evt in _bus.SubscribeAsync(ct))
        {
            // توزيع الحدث على كل التكاملات المتاحة
            foreach (var adapter in _adapters)
            {
                try
                {
                    _ = Task.Run(() => adapter.HandleEventAsync(evt, ct), ct); // عدم حظر المحرك
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Adapter {Name} failed to handle event {Type}", adapter.Name, evt.Type);
                }
            }
        }
    }
}
