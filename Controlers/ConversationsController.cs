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
        [FromQuery] string? search, [FromQuery] string? status, [FromQuery] string? assigned,
        [FromQuery] int page = 1, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var identityTenant = User.TenantId();
        if (!string.IsNullOrEmpty(tenantId) && tenantId != identityTenant) return Forbid();
        var q = _db.WhatsAppConversations.AsNoTracking().Where(c => c.TenantId == identityTenant);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status is not ("open" or "closed")) return BadRequest(new { error = "invalid_status" });
            q = q.Where(c => c.Status == status);
        }
        if (assigned == "me") { var name = User.Identity!.Name; q = q.Where(c => c.AssignedToUserId == name); }
        else if (assigned == "unassigned") q = q.Where(c => c.AssignedToUserId == null || c.AssignedToUserId == "");
        else if (!string.IsNullOrEmpty(assigned)) return BadRequest(new { error = "invalid_assignment_filter" });
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            if (search.Length > 100) return BadRequest(new { error = "search_too_long" });
            q = q.Where(c => c.ContactPhone.Contains(search) ||
                _db.CrmContacts.Any(p => p.TenantId == identityTenant && p.Phone == c.ContactPhone && p.DisplayName.Contains(search)));
        }
        page = Math.Clamp(page, 1, 10000); limit = Math.Clamp(limit, 1, 200);
        var list = await q
            .OrderByDescending(c => c.UpdatedAtUtc).ThenBy(c => c.Id)
            .Skip((page - 1) * limit).Take(limit)
            .Select(c => new
            {
                id = c.Id,
                tenantId = c.TenantId,
                phoneNumberId = c.PhoneNumberId,
                contactPhone = c.ContactPhone,
                status = c.Status,
                contactName = _db.CrmContacts.Where(p => p.TenantId == identityTenant && p.Phone == c.ContactPhone)
                    .Select(p => p.DisplayName).FirstOrDefault(),
                unreadCount = c.UnreadCount,
                assignedToUserId = c.AssignedToUserId,
                tags = c.Tags,
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

    public record StatusRequest(string? Status);
    public record AssignRequest(string? Username);
    public record TagsRequest(string? Tags);

    [Authorize(Policy = PlatformSecurity.ReadPolicy)]
    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, StatusRequest req, CancellationToken ct)
    {
        if (req.Status is not ("open" or "closed")) return BadRequest();
        var tenant = User.TenantId();
        var n = await _db.WhatsAppConversations.Where(c => c.Id == id && c.TenantId == tenant)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, req.Status!), ct);
        return n == 0 ? NotFound() : NoContent();
    }

    [Authorize(Policy = PlatformSecurity.ReadPolicy)]
    [HttpPut("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var n = await _db.WhatsAppConversations.Where(c => c.Id == id && c.TenantId == tenant)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UnreadCount, 0), ct);
        return n == 0 ? NotFound() : NoContent();
    }

    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [HttpPut("{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, AssignRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var q = _db.WhatsAppConversations.Where(c => c.Id == id && c.TenantId == tenant);
        if (!await q.AnyAsync(ct)) return NotFound();
        var username = string.IsNullOrWhiteSpace(req.Username) ? null : req.Username.Trim();
        if (username != null && !await _db.ManagedAgents.AnyAsync(a => a.TenantId == tenant && a.Username == username && a.IsActive, ct))
            return BadRequest(new { error = "invalid_agent" });
        var n = await q.ExecuteUpdateAsync(s => s.SetProperty(c => c.AssignedToUserId, username), ct);
        return n == 0 ? NotFound() : NoContent();
    }

    [Authorize(Policy = PlatformSecurity.ReadPolicy)]
    [HttpPut("{id:guid}/tags")]
    public async Task<IActionResult> SetTags(Guid id, TagsRequest req, CancellationToken ct)
    {
        if ((req.Tags?.Length ?? 0) > 300) return BadRequest();
        var tenant = User.TenantId();
        var tags = req.Tags?.Trim();
        var n = await _db.WhatsAppConversations.Where(c => c.Id == id && c.TenantId == tenant)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Tags, tags), ct);
        return n == 0 ? NotFound() : NoContent();
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
