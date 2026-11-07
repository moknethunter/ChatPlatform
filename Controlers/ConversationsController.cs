using ChatPlatform.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/conversations")]
public class ConversationsController : ControllerBase
{
    private readonly PlatformDbContext _db;
    public ConversationsController(PlatformDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? tenantId, CancellationToken ct)
    {
        var q = _db.WhatsAppConversations.AsNoTracking();
        if (!string.IsNullOrEmpty(tenantId)) q = q.Where(c => c.TenantId == tenantId);

        var list = await q
            .OrderByDescending(c => c.UpdatedAtUtc)
            .Select(c => new
            {
                id = c.Id,
                tenantId = c.TenantId,
                phoneNumberId = c.PhoneNumberId,
                contactPhone = c.ContactPhone,
                status = c.Status,
                lastInboundAtUtc = c.LastInboundAtUtc,
                windowExpiresAtUtc = c.WindowExpiresAtUtc,
                isWindowOpen = c.WindowExpiresAtUtc.HasValue && c.WindowExpiresAtUtc.Value > DateTime.UtcNow,
                updatedAtUtc = c.UpdatedAtUtc,
                lastMessage = _db.WhatsAppMessages
                    .Where(m => m.ConversationId == c.Id)
                    .OrderByDescending(m => m.TimestampUtc)
                    .Select(m => new { text = m.TextPreview, direction = m.Direction, status = m.Status, timestampUtc = m.TimestampUtc })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        return Ok(list);
    }

    [HttpGet("{id:Guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid id, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var msgs = await _db.WhatsAppMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderByDescending(m => m.TimestampUtc)
            .Take(Math.Min(take, 200))
            .Select(m => new
            {
                id = m.Id,
                direction = m.Direction,
                messageType = m.MessageType,
                textPreview = m.TextPreview,
                contentJson = m.ContentJson,
                status = m.Status,
                wamid = m.Wamid,
                timestampUtc = m.TimestampUtc
            })
            .ToListAsync(ct);

        msgs.Reverse();
        return Ok(msgs);
    }
}
