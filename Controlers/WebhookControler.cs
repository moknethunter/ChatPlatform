using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using Microsoft.AspNetCore.Mvc;

namespace ChatPlatform.Api.Controllers;

[ApiController]
[Route("webhook")]
public class WebhookController : ControllerBase
{
    private readonly IEventBus _bus;
    private readonly IConfiguration _cfg;

    // حقن ناقل الأحداث والإعدادات
    public WebhookController(IEventBus bus, IConfiguration cfg)
    {
        _bus = bus;
        _cfg = cfg;
    }

    // 1) التحقق من فيسبوك (Meta GET)
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string mode,
        [FromQuery(Name = "hub.verify_token")] string token,
        [FromQuery(Name = "hub.challenge")] string challenge)
    {
        var verifyToken = _cfg["WhatsApp:VerifyToken"] ?? "Mokhtar123";
        
        if (mode == "subscribe" && token == verifyToken)
        {
            return Ok(challenge);
        }
        return Forbid();
    }

    // 2) استقبال الرسائل (Meta POST)
    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] JsonElement body)
    {
        try
        {
            // تحليل هيكل رسالة الواتساب القادمة من Meta
            if (body.TryGetProperty("entry", out var entries))
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes)) continue;
                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value)) continue;
                        if (!value.TryGetProperty("messages", out var msgs)) continue;

                        foreach (var m in msgs.EnumerateArray())
                        {
                            if (!m.TryGetProperty("text", out var t) ||
                                !t.TryGetProperty("body", out var textBody)) continue;

                            var from = m.GetProperty("from").GetString();
                            var text = textBody.GetString();

                            // 🎯 النشر في ناقل الأحداث (هذا ما يشغل المنصة)
                            await _bus.PublishAsync(new PlatformEvent
                            {
                                Type = "whatsapp.inbound", // يتطابق مع WhatsAppAdapter
                                Source = "whatsapp",
                                Payload = JsonSerializer.SerializeToElement(new { from, text })
                            }, CancellationToken.None);
                        }
                    }
                }
            }
            return Ok();
        }
        catch (Exception)
        {
            // يجب دائماً إرجاع Ok لـ Meta حتى لا تعيد المحاولة وتغرق الخادم
            return Ok(); 
        }
    }
}
