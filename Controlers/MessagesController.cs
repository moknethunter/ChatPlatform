using ChatPlatform.Infrastructure.Subscriptions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using Microsoft.AspNetCore.DataProtection;
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

    public sealed record SendTemplateRequest(string? To, JsonElement Template, string? PhoneNumberId, string? TenantId, string? WhatsAppTenantId);

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

        var payload = JsonSerializer.Serialize(new { messaging_product = "whatsapp", recipient_type = "individual", to, type = "text", text = new { body = text, preview_url = false } });
        return await Queue(tenant, to, req.PhoneNumberId, req.WhatsAppTenantId, "text", text, payload, ct);
    }

    [HttpPost("send-template")]
    public async Task<IActionResult> SendTemplate([FromBody] SendTemplateRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        if (!string.IsNullOrEmpty(req.TenantId) && req.TenantId != tenant) return Forbid();
        var to = req.To?.Trim().TrimStart('+');
        if (string.IsNullOrEmpty(to) || to.Length is < 7 or > 15 || to[0] == '0'
            || to.Any(c => c < '0' || c > '9')) return BadRequest(new { error = "invalid_recipient" });
        var template = req.Template;
        if (template.ValueKind != JsonValueKind.Object ||
            !template.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String ||
            !template.TryGetProperty("language", out var lang) || lang.ValueKind != JsonValueKind.Object ||
            !lang.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.String)
            return BadRequest(new { error = "invalid_template" });
        var name = nameEl.GetString()!;
        var code = codeEl.GetString()!;
        if (name.Length is < 1 or > 512 || name.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            || code.Length is < 2 or > 20 || code.Any(c => !(char.IsAsciiLetter(c) || c == '_')))
            return BadRequest(new { error = "invalid_template" });
        JsonElement? components = null;
        if (template.TryGetProperty("components", out var comp))
        {
            if (comp.ValueKind != JsonValueKind.Array || comp.GetArrayLength() > 20 || comp.GetRawText().Length > 32000)
                return BadRequest(new { error = "invalid_components" });
            components = comp;
        }
        // Only the selected tenant's token can send this template. Meta remains authoritative for approval.
        var templatePayload = new Dictionary<string, object> { ["name"] = name, ["language"] = new { code } };
        if (components.HasValue && components.Value.GetArrayLength() > 0) templatePayload["components"] = components.Value;
        var payload = JsonSerializer.Serialize(new { messaging_product = "whatsapp", recipient_type = "individual", to, type = "template", template = templatePayload });
        return await Queue(tenant, to, req.PhoneNumberId, req.WhatsAppTenantId, "template", name, payload, ct);
    }

    public sealed record SendMediaRequest(Guid ConversationId, string? Ticket, string? Caption);

    [HttpPost("send-media")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("media")]
    public async Task<IActionResult> SendMedia(SendMediaRequest req, [FromServices] IDataProtectionProvider protection, CancellationToken ct)
    {
        var tenant = User.TenantId();
        if (string.IsNullOrWhiteSpace(req.Ticket) || req.Ticket.Length > 4096 || (req.Caption?.Length ?? 0) > 1024)
            return BadRequest(new { error = "invalid_media" });
        MediaController.Ticket? ticket;
        try {
            ticket = JsonSerializer.Deserialize<MediaController.Ticket>(
                protection.CreateProtector(MediaController.Purpose).ToTimeLimitedDataProtector().Unprotect(req.Ticket));
        } catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) {
            return BadRequest(new { error = "invalid_media_ticket" });
        }
        if (ticket == null || ticket.Tenant != tenant || ticket.ConversationId != req.ConversationId) return Forbid();
        var conv = await _db.WhatsAppConversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == req.ConversationId && c.TenantId == tenant, ct);
        if (conv == null || conv.PhoneNumberId != ticket.PhoneId) return NotFound();
        if (ticket.Type is not ("image" or "video" or "audio" or "document")) return BadRequest();
        var media = new Dictionary<string, object> { ["id"] = ticket.MediaId };
        if (ticket.Type != "audio" && !string.IsNullOrWhiteSpace(req.Caption)) media["caption"] = req.Caption.Trim();
        if (ticket.Type == "document") media["filename"] = ticket.Filename;
        var body = new Dictionary<string, object> { ["messaging_product"] = "whatsapp", ["recipient_type"] = "individual",
            ["to"] = conv.ContactPhone, ["type"] = ticket.Type, [ticket.Type] = media };
        return await Queue(tenant, conv.ContactPhone, ticket.PhoneId, null, ticket.Type, req.Caption?.Trim() ?? "["+ticket.Type+"]", JsonSerializer.Serialize(body), ct);
    }

    private async Task<IActionResult> Queue(string tenant, string to, string? phoneNumberId, string? whatsAppTenantId,
        string messageType, string preview, string payload, CancellationToken ct)
    {
        var query = _db.WhatsAppTenants.AsNoTracking().Where(a => a.TenantId == tenant && a.IsActive);
        if (!string.IsNullOrEmpty(phoneNumberId)) query = query.Where(a => a.PhoneNumberId == phoneNumberId);
        if (!string.IsNullOrEmpty(whatsAppTenantId)) query = query.Where(a => a.Id == whatsAppTenantId);
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
        async Task<IActionResult?> Existing()
        {
            var existing = await _db.WhatsAppOutbox.AsNoTracking()
                .SingleOrDefaultAsync(o => o.TenantId == tenant && o.IdempotencyKey == key, ct);
            if (existing == null) return null;
            if (existing.PayloadJson != payload || existing.PhoneNumberId != account.PhoneNumberId)
                return Conflict(new { error = "idempotency_key_reused" });
            return Accepted(new { id = existing.MessageId, status = existing.Status });
        }
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var previous = await Existing();
        if (previous != null) return previous;
        if(await Entitlements.CapacityAsync(_db,tenant,"messages",1,now,ct) is string denied)return Conflict(new{error=denied});

        var conversation = await _db.WhatsAppConversations.FirstOrDefaultAsync(c =>
            c.TenantId == tenant && c.PhoneNumberId == account.PhoneNumberId && c.ContactPhone == to, ct);
        if (messageType != "template" && (conversation?.LastInboundAtUtc is not DateTime inbound || inbound > now || now - inbound >= TimeSpan.FromHours(24)))
            return Conflict(new { error = "window_closed_use_template" });
        if (conversation == null)
        {
            conversation = new WhatsAppConversationEntity { TenantId = tenant, PhoneNumberId = account.PhoneNumberId,
                ContactPhone = to, CreatedAtUtc = now, UpdatedAtUtc = now };
            _db.WhatsAppConversations.Add(conversation);
        }

        var message = new WhatsAppMessageEntity
        {
            TenantId = tenant, ConversationId = conversation.Id, Direction = "Outbound", MessageType = messageType,
            ContentJson = payload, TextPreview = preview.Length > 200 ? preview[..200] : preview,
            Status = "Queued", TimestampUtc = now, CreatedAtUtc = now
        };
        _db.WhatsAppMessages.Add(message);
        _db.WhatsAppOutbox.Add(new WhatsAppOutboxEntity
        {
            TenantId = tenant, IdempotencyKey = key, MessageId = message.Id,
            PhoneNumberId = account.PhoneNumberId, RecipientPhone = to, MessageType = messageType,
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
        await tx.CommitAsync(ct);
        return Accepted(new { id = message.Id, status = message.Status, conversationId = conversation.Id });
    }
}
