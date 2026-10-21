using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters;

public sealed class UniversalConnectorAdapter : IIntegrationAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UniversalConnectorAdapter> _log;

    public UniversalConnectorAdapter(IHttpClientFactory httpClientFactory, ILogger<UniversalConnectorAdapter> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public string Name => "Universal Engine";

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        // نتحقق أن الحدث هو رسالة من التطبيق
        if (evt.Type != "message.created" || evt.Source != "chat-app") return;

        var messageText = evt.Payload.TryGetProperty("Text", out var t) ? t.GetString() : "";
        var recipient = evt.Payload.TryGetProperty("Recipient", out var r) ? r.GetString() : "";

        if (DynamicSettings.Connectors.Count == 0) return;

        var client = _httpClientFactory.CreateClient();

        // نمر على كل التكاملات التي أضافها المستخدم من لوحة التحكم
        foreach (var connector in DynamicSettings.Connectors)
        {
            try
            {
                // استبدال المتغيرات في قالب البيانات
                var payload = connector.PayloadTemplate
                    .Replace("{Message}", messageText)
                    .Replace("{Recipient}", recipient);

                var request = new HttpRequestMessage(new HttpMethod(connector.HttpMethod), connector.TargetUrl);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                // إضافة التوكن
                if (!string.IsNullOrEmpty(connector.AuthToken))
                {
                    // بعض المنصات تحتاج كلمة Bearer قبل التوكن
                    var tokenValue = connector.AuthHeaderKey.Equals("Authorization", StringComparison.OrdinalIgnoreCase) 
                                     && !connector.AuthToken.StartsWith("Bearer") 
                                     ? $"Bearer {connector.AuthToken}" 
                                     : connector.AuthToken;
                                     
                    request.Headers.TryAddWithoutValidation(connector.AuthHeaderKey, tokenValue);
                }

                var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                    _log.LogInformation("✅ تم الإرسال بنجاح عبر {Name}", connector.Name);
                else
                    _log.LogError("❌ فشل الإرسال عبر {Name}: {Error}", connector.Name, await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "خطأ في التكامل {Name}", connector.Name);
            }
        }
    }
}
