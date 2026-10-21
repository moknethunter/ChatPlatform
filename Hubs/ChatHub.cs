using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using Microsoft.AspNetCore.SignalR;

namespace ChatPlatform.Hubs;

public sealed class ChatHub : Hub
{
    private readonly IEventBus _bus;

    public ChatHub(IEventBus bus) => _bus = bus;

    public Task Join(string channel) => Groups.AddToGroupAsync(Context.ConnectionId, channel);

    public async Task Send(string channel, string text)
    {
        var evt = new PlatformEvent
        {
            Type = "message.created",
            Source = "chat-app",
            Payload = JsonSerializer.SerializeToElement(new { ChannelId = channel, Text = text })
        };
        await _bus.PublishAsync(evt);
    }
}
