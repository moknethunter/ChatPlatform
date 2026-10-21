using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;

namespace ChatPlatform.Infrastructure.Adapters;

public sealed class WebhookAdapter : IIntegrationAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    
    // قاموس لتخزين روابط الويب هوك المسجلة ديناميكياً
    public static readonly ConcurrentDictionary<string, string> RegisteredWebhooks = new();

    public WebhookAdapter(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public string Name => "WebhookDispatcher";

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (RegisteredWebhooks.IsEmpty) return;

        var client = _httpClientFactory.CreateClient("webhooks");
        var payload = JsonSerializer.Serialize(evt);

        foreach (var webhook in RegisteredWebhooks)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, webhook.Value)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("X-Webhook-Event", evt.Type);
                await client.SendAsync(req, ct);
            }
            catch { /* تسجيل الخطأ وتجاهله لعدم إيقاف النواة */ }
        }
    }
}
