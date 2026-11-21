using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/messages")]
[Authorize(Policy = PlatformSecurity.ReadPolicy)]
public sealed class MessagesController : ControllerBase
{
    private readonly PlatformDbContext _db;
    public MessagesController(PlatformDbContext db) => _db = db;

    public sealed record SendTextRequest(string? To, JsonElement Text, string? PhoneNumberId,
        string? TenantId, string? WhatsAppTenantId, string? Type);

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendTextRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        if (!string.IsNullOrEmpty(req.TenantId) && req.TenantId != tenant) return Forbid();
        if (!string.IsNullOrEmpty(req.Type) && req.Type != "text") return BadRequest(new { error = "text_only" });
        var to = req.To?.Trim().TrimStart('+');
        if (string.IsNullOrEmpty(to) || to.Length is < 7 or > 15 || to[0] == '0'
            || to.Any(c => c < '0' || c > '9')) return BadRequest(new { error = "invalid_recipient" });
        var value = req.Text;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("body", out var body)) value = body;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) return BadRequest(new { error = "invalid_text" });

        var query = _db.WhatsAppTenants.AsNoTracking().Where(a => a.TenantId == tenant && a.IsActive);
        if (!string.IsNullOrEmpty(req.PhoneNumberId)) query = query.Where(a => a.PhoneNumberId == req.PhoneNumberId);
        if (!string.IsNullOrEmpty(req.WhatsAppTenantId)) query = query.Where(a => a.Id == req.WhatsAppTenantId);
        var accounts = await query.Take(2).ToListAsync(ct);
        if (accounts.Count == 0) return NotFound(new { error = "account_not_found" });
        if (accounts.Count != 1) return BadRequest(new { error = "select_account" });
        var account = accounts[0];
        if (string.IsNullOrWhiteSpace(account.BusinessTokenEncrypted)) return Conflict(new { error = "account_not_configured" });

        var now = DateTime.UtcNow;
        // Bind the idempotency key to the verified tenant, never another caller's data.
        var suppliedKey = Request.Headers["Idempotency-Key"].ToString();
        if (suppliedKey.Length > 128) return BadRequest(new { error = "invalid_idempotency_key" });
        if (string.IsNullOrEmpty(suppliedKey)) suppliedKey = Guid.NewGuid().ToString("N");
        var key = "api:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenant + "\n" + suppliedKey)));
        var payload = JsonSerializer.Serialize(new { messaging_product = "whatsapp", recipient_type = "individual", to, type = "text", text = new { body = text, preview_url = false } });
        async Task<IActionResult?> Existing()
        {
            var existing = await _db.WhatsAppOutbox.AsNoTracking()
                .SingleOrDefaultAsync(o => o.TenantId == tenant && o.IdempotencyKey == key, ct);
            if (existing == null) return null;
            if (existing.PayloadJson != payload || existing.PhoneNumberId != account.PhoneNumberId)
                return Conflict(new { error = "idempotency_key_reused" });
            return Accepted(new { id = existing.MessageId, status = existing.Status });
        }
        var previous = await Existing();
        if (previous != null) return previous;

        var conversation = await _db.WhatsAppConversations.FirstOrDefaultAsync(c =>
            c.TenantId == tenant && c.PhoneNumberId == account.PhoneNumberId && c.ContactPhone == to, ct);
        if (conversation?.LastInboundAtUtc is not DateTime inbound || inbound > now || now - inbound >= TimeSpan.FromHours(24))
            return Conflict(new { error = "window_closed_use_template" });

        var message = new WhatsAppMessageEntity
        {
            TenantId = tenant, ConversationId = conversation.Id, Direction = "Outbound", MessageType = "text",
            ContentJson = payload, TextPreview = text.Length > 200 ? text[..200] : text,
            Status = "Queued", TimestampUtc = now, CreatedAtUtc = now
        };
        _db.WhatsAppMessages.Add(message);
        _db.WhatsAppOutbox.Add(new WhatsAppOutboxEntity
        {
            TenantId = tenant, IdempotencyKey = key, MessageId = message.Id,
            PhoneNumberId = account.PhoneNumberId, RecipientPhone = to, MessageType = "text",
            PayloadJson = payload, Status = "Pending", CreatedAtUtc = now, NextAttemptAtUtc = now, UpdatedAtUtc = now
        });
        conversation.UpdatedAtUtc = now;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // SaveChanges is atomic. On a competing request, return only the matching queued message.
            _db.ChangeTracker.Clear();
            var duplicate = await Existing();
            if (duplicate != null) return duplicate;
            throw;
        }
        return Accepted(new { id = message.Id, status = message.Status, conversationId = conversation.Id });
    }
}
