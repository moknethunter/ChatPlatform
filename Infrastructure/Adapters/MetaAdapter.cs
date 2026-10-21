using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters;

public sealed class MetaAdapter : IIntegrationAdapter
{
    private readonly ILogger<MetaAdapter> _log;

    public MetaAdapter(ILogger<MetaAdapter> log) => _log = log;

    public string Name => "Meta (WhatsApp/Facebook)";

    public Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        // هنا يتم وضع كود إرسال الرسالة الفعلي إلى WhatsApp Cloud API أو Facebook Graph API
        // نستخدم الـ Log الآن كدليل على وصول الرسالة
        
        if (evt.Type == "message.created" && evt.Source == "chat-app")
        {
            _log.LogInformation("📱 [Meta API] Sending message to WhatsApp/Facebook: {Payload}", evt.Payload.ToString());
            // TODO: await _httpClient.PostAsync("https://graph.facebook.com/v18.0/.../messages", ...);
        }
        return Task.CompletedTask;
    }
}
