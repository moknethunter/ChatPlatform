using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ChatPlatform.Module.Chat.Domain.Entities;
using ChatPlatform.Module.Chat.Infrastructure.Persistence;

namespace ChatPlatform.Api.Controllers;

[ApiController]
[Route("api/whatsapp/webhook")]
public class WhatsAppWebhookController : ControllerBase
{
    private readonly ChatDbContext _db;
    private readonly ILogger<WhatsAppWebhookController> _logger;

    // لازم يطابق Verify Token في Meta
    private const string VERIFY_TOKEN = "mokco-verify-123";

    public WhatsAppWebhookController(
        ChatDbContext db,
        ILogger<WhatsAppWebhookController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // =====================================================
    // 1. Meta ترسل GET للتحقق - Verify and Save
    // Meta ترسل:
    // ?hub.mode=subscribe&hub.verify_token=mokco-verify-123&hub.challenge=XXXX
    // =====================================================
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        _logger.LogInformation("WhatsApp Verify: mode={Mode} token={Token}", mode, verifyToken);

        if (mode == "subscribe" && verifyToken == VERIFY_TOKEN)
        {
            // لازم نرجع challenge كنص خام، مو JSON
            return Content(challenge ?? "", "text/plain");
        }

        return Forbid();
    }

    // =====================================================
    // 2. Meta ترسل POST عند كل رسالة واتساب جديدة
    // لازم نرد 200 بسرعة، وإلا Meta تعيد الإرسال 3 مرات
    // =====================================================
    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] WhatsAppWebhookPayload? payload)
    {
        try
        {
            var value = payload?.Entry?.FirstOrDefault()
                ?.Changes?.FirstOrDefault()
                ?.Value;

            if (value == null)
            {
                // هذا يصير مع رسائل الحالة statuses - مو رسائل حقيقية
                return Ok();
            }

            var waMsg = value.Messages?.FirstOrDefault();
            if (waMsg == null)
            {
                return Ok();
            }

            var from = waMsg.From ?? "unknown";
            var text = waMsg.Text?.Body
                ?? waMsg.Image?.Caption
                ?? waMsg.Document?.Caption
                ?? "[رسالة غير نصية]";

            _logger.LogInformation("WhatsApp من {From}: {Text}", from, text);

            // --- حول رسالة واتساب لرسالة داخلية ---
            // نبحث عن محادثة بنفس رقم الواتساب، إذا ما فيه ننشئ وحدة جديدة
            // Channel = WhatsApp - هذا هو سر التوسع اللي بنيناه من أول يوم
            var conversation = await _db.Conversations
                .FirstOrDefaultAsync(c => c.ExternalId == from && c.Channel == "WhatsApp");

            if (conversation == null)
            {
                conversation = new Conversation
                {
                    Id = Guid.NewGuid(),
                    Channel = "WhatsApp",
                    ExternalId = from,
                    Title = from,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Conversations.Add(conversation);
                await _db.SaveChangesAsync();
            }

            // منع التكرار - Meta ممكن ترسل نفس الرسالة مرتين
            var exists = await _db.Messages
                .AnyAsync(m => m.ExternalId == waMsg.Id);
            if (exists)
            {
                return Ok();
            }

            var message = new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                SenderId = from,
                Content = text,
                Channel = "WhatsApp",
                ExternalId = waMsg.Id,
                CreatedAt = DateTime.UtcNow
            };

            _db.Messages.Add(message);
            await _db.SaveChangesAsync();

            // هنا لاحقاً ننشر على SignalR:
            // await _hub.Clients.Group(conversation.Id.ToString())
            //     .SendAsync("NewMessage", message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp Webhook Error");
            // حتى لو صار خطأ، نرد Ok عشان Meta ما تعيد الإرسال بلا نهاية
        }

        return Ok();
    }
}

// =====================================================
// DTOs - شكل JSON اللي ترسله Meta
// =====================================================
public class WhatsAppWebhookPayload
{
    [JsonPropertyName("entry")]
    public List<WhatsAppEntry>? Entry { get; set; }
}

public class WhatsAppEntry
{
    [JsonPropertyName("changes")]
    public List<WhatsAppChange>? Changes { get; set; }
}

public class WhatsAppChange
{
    [JsonPropertyName("value")]
    public WhatsAppValue? Value { get; set; }
}

public class WhatsAppValue
{
    [JsonPropertyName("messages")]
    public List<WhatsAppMessage>? Messages { get; set; }

    [JsonPropertyName("statuses")]
    public List<object>? Statuses { get; set; }
}

public class WhatsAppMessage
{
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("text")]
    public WhatsAppText? Text { get; set; }

    [JsonPropertyName("image")]
    public WhatsAppMedia? Image { get; set; }

    [JsonPropertyName("document")]
    public WhatsAppMedia? Document { get; set; }
}

public class WhatsAppText
{
    [JsonPropertyName("body")]
    public string? Body { get; set; }
}

public class WhatsAppMedia
{
    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }
}
