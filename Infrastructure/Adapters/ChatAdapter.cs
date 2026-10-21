using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChatPlatform.Infrastructure.Adapters;

public sealed class ChatAdapter : IIntegrationAdapter
{
    private readonly IHubContext<ChatHub> _hub;

    public ChatAdapter(IHubContext<ChatHub> hub) => _hub = hub;

    public string Name => "ChatApp";

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (evt.Type == "message.created" && evt.Payload.TryGetProperty("ChannelId", out var ch))
        {
            var channel = ch.GetString();
            await _hub.Clients.Group(channel!).SendAsync("message.created", evt, ct);
        }
        else
        {
            // أي حدث آخر، يرسله للجميع في الشات
            await _hub.Clients.All.SendAsync("platform.event", evt, ct);
        }
    }
}
