using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters;

public sealed class MetaAdapter : IIntegrationAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MetaAdapter> _log;

    public MetaAdapter(IHttpClientFactory httpClientFactory, ILogger<MetaAdapter> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public string Name => "Meta (WhatsApp)";

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (evt.Type == "message.created" && evt.Source == "chat-app")
        {
            // التحقق من وجود الإعدادات
            if (string.IsNullOrEmpty(DynamicSettings.WhatsappPhoneId) || string.IsNullOrEmpty(DynamicSettings.WhatsappToken))
            {
                _log.LogWarning("⚠️ إعدادات واتساب غير مكتملة. يرجى ضبطها من لوحة التحكم.");
                return;
            }

            try
            {
                var text = evt.Payload.TryGetProperty("Text", out var t) ? t.GetString() : "";
                var recipientKey = evt.Payload.TryGetProperty("Recipient", out var r) ? r.GetString() : "";

                // جلب الرقم الفعلي من قائمة الأرقام المسجلة
                if (string.IsNullOrEmpty(recipientKey) || !DynamicSettings.RegisteredNumbers.TryGetValue(recipientKey, out var recipientPhone))
                {
                    _log.LogWarning("⚠️ لم يتم العثور على رقم المستلم المسجل.");
                    return;
                }

                var client = _httpClientFactory.CreateClient();
                var url = $"https://graph.facebook.com/v22.0/{DynamicSettings.WhatsappPhoneId}/messages";
                
                var payload = new
                {
                    messaging_product = "whatsapp",
                    to = recipientPhone,
                    type = "text",
                    text = new { body = text }
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                request.Headers.Add("Authorization", $"Bearer {DynamicSettings.WhatsappToken}");

                var response = await client.SendAsync(request, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                    _log.LogInformation("✅ تم إرسال رسالة واتساب إلى {Recipient} بنجاح", recipientPhone);
                else
                    _log.LogError("❌ خطأ من واتساب: {Error}", responseBody);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "فشل إرسال رسالة واتساب");
            }
        }
    }
}
