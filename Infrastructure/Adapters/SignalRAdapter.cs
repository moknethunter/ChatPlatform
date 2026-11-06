
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChatPlatform.Infrastructure.Adapters;

public sealed class SignalRAdapter : IIntegrationAdapter
{
    public string Name => "signalr";
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<SignalRAdapter> _log;

    public SignalRAdapter(IHubContext<ChatHub> hub, ILogger<SignalRAdapter> log)
    {
        _hub = hub;
        _log = log;
    }

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (evt.Type?.StartsWith("whatsapp.", StringComparison.OrdinalIgnoreCase) != true) return;

        try
        {
            await _hub.Clients.All.SendAsync("WhatsAppEvent", new
            {
                type = evt.Type,
                payload = evt.Payload,
                timestamp = DateTime.UtcNow
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SignalR push failed for {Type}", evt.Type);
        }
    }
}
