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
        // Do not relay internal events or customer messages to unauthenticated SignalR clients.
        if (evt.Type == "message.created" && evt.Source == "chat-app" &&
            evt.Payload.ValueKind == JsonValueKind.Object &&
            evt.Payload.TryGetProperty("ChannelId", out var ch) &&
            ch.ValueKind == JsonValueKind.String)
        {
            var channel = ch.GetString();
            if (!string.IsNullOrWhiteSpace(channel))
                await _hub.Clients.Group(channel).SendAsync("message.created", evt, ct);
        }
    }
}
