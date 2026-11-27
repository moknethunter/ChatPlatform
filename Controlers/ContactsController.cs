using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController, Route("api/contacts")]
[Authorize(Policy = PlatformSecurity.ReadPolicy)]
public sealed class ContactsController(PlatformDbContext db) : ControllerBase
{
    public record ContactRequest(string? Phone, string? DisplayName, string? Email, string? Notes, string? Tags, string? Stage);
    private static bool ValidPhone(string? s) => s is { Length: >= 7 and <= 15 } && s[0] != '0' && s.All(c => c is >= '0' and <= '9');
    private static bool ValidStage(string? s) => s is "new" or "lead" or "customer" or "inactive";
    private static bool Valid(ContactRequest r) =>
        (r.DisplayName?.Length ?? 0) <= 100 && (r.Email?.Length ?? 0) <= 254 &&
        (r.Notes?.Length ?? 0) <= 4000 && (r.Tags?.Length ?? 0) <= 300 &&
        (r.Stage == null || ValidStage(r.Stage));
    private static void Apply(CrmContactEntity row, ContactRequest req)
    {
        row.DisplayName = req.DisplayName?.Trim() ?? "";
        row.Email = req.Email?.Trim() ?? "";
        row.Notes = req.Notes?.Trim() ?? "";
        row.Tags = req.Tags?.Trim() ?? "";
        row.Stage = req.Stage ?? "new";
        row.UpdatedAtUtc = DateTime.UtcNow;
    }

    [HttpGet]
    public async Task<IActionResult> List(string? search, string? stage, int page = 1, int limit = 50, CancellationToken ct = default)
    {
        var tenant = User.TenantId();
        var q = db.CrmContacts.AsNoTracking().Where(c => c.TenantId == tenant);
        if (!string.IsNullOrWhiteSpace(stage))
        {
            if (!ValidStage(stage)) return BadRequest(new { error = "invalid_stage" });
            q = q.Where(c => c.Stage == stage);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            if (search.Length > 100) return BadRequest(new { error = "search_too_long" });
            q = q.Where(c => c.Phone.Contains(search) || c.DisplayName.Contains(search) || c.Tags.Contains(search) || c.Email.Contains(search));
        }
        page = Math.Clamp(page, 1, 10000); limit = Math.Clamp(limit, 1, 100);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(c => c.UpdatedAtUtc).ThenBy(c => c.Phone)
            .Skip((page - 1) * limit).Take(limit).Select(c => new {
                c.Phone, c.DisplayName, c.Email, c.Tags, c.Stage, c.UpdatedAtUtc
            }).ToListAsync(ct);
        return Ok(new { total, page, limit, items });
    }

    [HttpGet("{phone}")]
    public async Task<IActionResult> Get(string phone, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var row = await db.CrmContacts.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenant && c.Phone == phone, ct);
        if (row == null) return NotFound();
        return Ok(row);
    }

    // A bounded, read-only timeline. Never infer consent from the presence of messages.
    [HttpGet("{phone}/activity")]
    public async Task<IActionResult> Activity(string phone, CancellationToken ct)
    {
        var tenant = User.TenantId();
        if (!ValidPhone(phone)) return BadRequest(new { error = "invalid_phone" });
        if (!await db.CrmContacts.AnyAsync(c => c.TenantId == tenant && c.Phone == phone, ct))
            return NotFound();

        var conversations = db.WhatsAppConversations.AsNoTracking()
            .Where(c => c.TenantId == tenant && c.ContactPhone == phone);
        var count = await conversations.CountAsync(ct);
        var open = await conversations.CountAsync(c => c.Status == "open", ct);
        var recentConversations = await conversations.OrderByDescending(c => c.UpdatedAtUtc).ThenBy(c => c.Id)
            .Take(50).Select(c => new { c.Id, c.PhoneNumberId, c.Status, c.AssignedToUserId,
                c.UnreadCount, c.LastInboundAtUtc, c.LastOutboundAtUtc, c.UpdatedAtUtc,
                automationMode = db.ConversationAutomation.Where(s => s.TenantId == tenant && s.ConversationId == c.Id)
                    .Select(s => s.Mode).FirstOrDefault() ?? "bot" }).ToListAsync(ct);
        var recentMessages = await (from m in db.WhatsAppMessages.AsNoTracking()
            join conv in conversations on m.ConversationId equals conv.Id
            where m.TenantId == tenant
            orderby m.TimestampUtc descending, m.Id descending
            select new { m.Id, m.ConversationId, conv.PhoneNumberId, m.Direction,
                m.MessageType, m.TextPreview, m.Status, m.TimestampUtc }).Take(30).ToListAsync(ct);

        // Marketing permission is visible only to administrators; no consent evidence or other tenant's data.
        object? consent = null;
        if (User.IsInRole("Admin"))
        {
            consent = await db.WhatsAppTenants.AsNoTracking().Where(a => a.TenantId == tenant && a.IsActive)
                .OrderBy(a => a.PhoneNumberId).Take(100)
                .Select(a => new { a.PhoneNumberId,
                    granted = db.MarketingConsents.Where(m => m.TenantId == tenant && m.Phone == phone &&
                        m.PhoneNumberId == a.PhoneNumberId).Select(m => (bool?)m.Granted).FirstOrDefault(),
                    updatedAtUtc = db.MarketingConsents.Where(m => m.TenantId == tenant && m.Phone == phone &&
                        m.PhoneNumberId == a.PhoneNumberId).Select(m => (DateTime?)m.UpdatedAtUtc).FirstOrDefault()
                }).ToListAsync(ct);
        }
        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new { conversationCount = count, openConversationCount = open,
            conversations = recentConversations, recentMessages, consent });
    }

    [HttpPost]
    public async Task<IActionResult> Create(ContactRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var phone = req.Phone?.Trim().TrimStart('+');
        if (!ValidPhone(phone) || !Valid(req)) return BadRequest(new { error = "invalid_contact" });
        if (await db.CrmContacts.AnyAsync(c => c.TenantId == tenant && c.Phone == phone, ct))
            return Conflict(new { error = "contact_exists" });
        var row = new CrmContactEntity { TenantId = tenant, Phone = phone!, CreatedAtUtc = DateTime.UtcNow };
        Apply(row, req); db.CrmContacts.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.CrmContacts.AnyAsync(c => c.TenantId == tenant && c.Phone == phone, ct))
                return Conflict(new { error = "contact_exists" });
            throw;
        }
        return StatusCode(201, row);
    }

    [HttpPut("{phone}")]
    public async Task<IActionResult> Update(string phone, ContactRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        if (!Valid(req) || (req.Phone != null && req.Phone != phone)) return BadRequest(new { error = "invalid_contact" });
        var row = await db.CrmContacts.SingleOrDefaultAsync(c => c.TenantId == tenant && c.Phone == phone, ct);
        if (row == null) return NotFound();
        Apply(row, req);
        await db.SaveChangesAsync(ct);
        return Ok(row);
    }
}
