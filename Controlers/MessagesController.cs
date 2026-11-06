using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/messages")]
public class MessagesController : ControllerBase
{
    private readonly IEventBus _bus;
    private readonly PlatformDbContext _db;

    public MessagesController(IEventBus bus, PlatformDbContext db)
    {
        _bus = bus;
        _db = db;
    }

    public record SendTextRequest(string To, string Text, string? PhoneNumberId);

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendTextRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.To) || string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "To and Text are required" });

        var evt = new PlatformEvent
        {
            Type = WhatsAppAdapter.OutboundType,
            Source = "api",
            Payload = JsonSerializer.SerializeToElement(new
            {
                phoneNumberId = req.PhoneNumberId,
                to = req.To,
                text = req.Text
            })
        };

        await _bus.PublishAsync(evt, ct);

        // انتظار قصير حتى يعالج المحوّل الحدث ويُنشئ الرسالة
        var textPreview = req.Text.Length > 200 ? req.Text[..200] : req.Text;
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(100, ct);
            var msg = await _db.WhatsAppMessages
                .AsNoTracking()
                .Where(m => m.Direction == "Outbound" && m.TextPreview == textPreview)
                .OrderByDescending(m => m.Id)
                .FirstOrDefaultAsync(ct);

            if (msg != null)
                return Ok(new
                {
                    id = msg.Id,
                    conversationId = msg.ConversationId,
                    status = msg.Status,
                    textPreview = msg.TextPreview,
                    timestampUtc = msg.TimestampUtc
                });
        }

        return Accepted(new { status = "queued", message = "Message queued for delivery" });
    }
}
