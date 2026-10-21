using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;

namespace ChatPlatform.Infrastructure.Adapters;

public sealed class WebhookAdapter : IIntegrationAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    
    // قائمة مؤقتة في الذاكرة لتخزين روابط الويب هوك (يمكن تحويلها لقاعدة بيانات لاحقاً)
    public static readonly List<string> TargetUrls = new();

    public WebhookAdapter(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public string Name => "WebhookSender";

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (TargetUrls.Count == 0) return;

        var client = _httpClientFactory.CreateClient("webhooks");
        var payload = JsonSerializer.Serialize(evt);

        foreach (var url in TargetUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("X-Webhook-Event", evt.Type);
                await client.SendAsync(req, ct);
            }
            catch { /* تجاهل الأخطاء في الروابط الخارجية */ }
        }
    }
}
