using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
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

    // ═══════════════════════════════════════════════════════════
    // GET /api/conversations
    // يعيد قائمة المحادثات مرتبة بالأحدث، مع آخر رسالة
    // ═══════════════════════════════════════════════════════════
    [Authorize(Policy = PlatformSecurity.ReadPolicy)]
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? tenantId,
        CancellationToken ct)
    {
        var identityTenant = User.TenantId();
        if (!string.IsNullOrEmpty(tenantId) && tenantId != identityTenant) return Forbid();
        var q = _db.WhatsAppConversations.AsNoTracking().Where(c => c.TenantId == identityTenant);

        var list = await q
            .OrderByDescending(c => c.UpdatedAtUtc)
            .Take(200)
            .Select(c => new
            {
                id = c.Id,
                tenantId = c.TenantId,
                phoneNumberId = c.PhoneNumberId,
                contactPhone = c.ContactPhone,
                status = c.Status,
                lastInboundAtUtc = c.LastInboundAtUtc,
                windowExpiresAtUtc = c.WindowExpiresAtUtc,
                isWindowOpen = c.WindowExpiresAtUtc.HasValue
                            && c.WindowExpiresAtUtc.Value > DateTime.UtcNow,
                updatedAtUtc = c.UpdatedAtUtc,
                lastMessage = _db.WhatsAppMessages
                    .Where(m => m.ConversationId == c.Id && m.TenantId == identityTenant)
                    .OrderByDescending(m => m.TimestampUtc)
                    .Select(m => new
                    {
                        text = m.TextPreview,
                        direction = m.Direction,
                        status = m.Status,
                        timestampUtc = m.TimestampUtc
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        return Ok(list);
    }

    // ═══════════════════════════════════════════════════════════
    // GET /api/conversations/{id}/messages
    // يعيد رسائل محادثة محددة، مرتبة تصاعدياً (الأقدم أولاً)
    // ═══════════════════════════════════════════════════════════
    [Authorize(Policy = PlatformSecurity.ReadPolicy)]
    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> GetMessages(
        Guid id,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        // ✅ تحقق من take (بين 1 و 200)
        var safeTake = Math.Clamp(take, 1, 200);
        var tenant = User.TenantId();
        if (!await _db.WhatsAppConversations.AnyAsync(c => c.Id == id && c.TenantId == tenant, ct)) return NotFound();

        var msgs = await _db.WhatsAppMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == id && m.TenantId == tenant)
            .OrderByDescending(m => m.TimestampUtc)
            .Take(safeTake)
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

        // نعكس الترتيب ليظهر الأقدم أولاً في الواجهة
        msgs.Reverse();

        return Ok(msgs);
    }
}
