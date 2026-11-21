using System.Security.Cryptography;
using System.Text.Json;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController, Route("api/media")]
[Authorize(Policy = PlatformSecurity.ReadPolicy)]
public sealed class MediaController(PlatformDbContext db, WhatsAppMediaService media, IDataProtectionProvider protection) : ControllerBase
{
    internal sealed record Ticket(string Tenant, Guid ConversationId, string PhoneId, string MediaId, string Type, string Filename);
    internal const string Purpose = "ChatPlatform.MediaTicket.v1";

    [HttpPost("upload/{conversationId:guid}")]
    [RequestSizeLimit(WhatsAppMediaService.MaxBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = WhatsAppMediaService.MaxBytes + 65536)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("media")]
    public async Task<IActionResult> Upload(Guid conversationId, [FromForm] IFormFile? file, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var conv = await db.WhatsAppConversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == conversationId && c.TenantId == tenant, ct);
        if (conv == null) return NotFound();
        if (file == null || file.Length <= 0 || file.Length > WhatsAppMediaService.MaxBytes) return BadRequest(new { error = "invalid_media_size" });
        var now = DateTime.UtcNow;
        if (conv.LastInboundAtUtc is not DateTime inbound || inbound > now || now-inbound >= TimeSpan.FromHours(24))
            return Conflict(new { error = "window_closed_use_template" });
        var account = await db.WhatsAppTenants.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.PhoneNumberId == conv.PhoneNumberId && a.IsActive, ct);
        if (account == null || string.IsNullOrWhiteSpace(account.BusinessTokenEncrypted)) return Conflict(new { error = "account_not_configured" });
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var kind = WhatsAppMediaService.Detect(bytes);
        if (kind == null) return BadRequest(new { error = "unsupported_media" });
        try
        {
            var token = new WhatsAppTokenProtection(protection).Unprotect(account.BusinessTokenEncrypted);
            var id = await media.UploadAsync(account.PhoneNumberId, token, bytes, kind, ct);
            if (id == null) return StatusCode(502, new { error = "media_upload_failed" });
            var ticket = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector()
                .Protect(JsonSerializer.Serialize(new Ticket(tenant, conversationId, account.PhoneNumberId, id, kind.Type, kind.Filename)), TimeSpan.FromMinutes(15));
            return Ok(new { ticket, type = kind.Type });
        }
        catch (Exception ex) when (ex is CryptographicException or HttpRequestException or JsonException or OperationCanceledException or FormatException)
        {
            return StatusCode(502, new { error = "media_upload_failed" });
        }
    }

    [HttpGet("{messageId:guid}")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("media")]
    public async Task<IActionResult> Download(Guid messageId, bool attachment = false, CancellationToken ct = default)
    {
        var tenant = User.TenantId();
        var row = await (from m in db.WhatsAppMessages.AsNoTracking()
                         join c in db.WhatsAppConversations.AsNoTracking() on m.ConversationId equals c.Id
                         where m.Id == messageId && m.TenantId == tenant && c.TenantId == tenant
                         select new { m.ContentJson, m.MessageType, c.PhoneNumberId }).SingleOrDefaultAsync(ct);
        if (row == null || row.MessageType is not ("image" or "audio" or "video" or "document")) return NotFound();
        var account = await db.WhatsAppTenants.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.PhoneNumberId == row.PhoneNumberId && a.IsActive, ct);
        if (account == null || string.IsNullOrWhiteSpace(account.BusinessTokenEncrypted)) return Conflict(new { error = "account_not_configured" });
        try
        {
            using var doc = JsonDocument.Parse(row.ContentJson);
            if (!doc.RootElement.TryGetProperty(row.MessageType, out var payload) || payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !WhatsAppMediaService.ValidId(id.GetString()))
                return NotFound();
            var token = new WhatsAppTokenProtection(protection).Unprotect(account.BusinessTokenEncrypted);
            var result = await media.DownloadAsync(account.PhoneNumberId, id.GetString()!, token, row.MessageType, ct);
            if (result == null) return StatusCode(502, new { error = "media_unavailable" });
            Response.Headers["Cache-Control"] = "private, no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            return File(result.Bytes, result.Kind.Mime, attachment || result.Kind.Type == "document" ? result.Kind.Filename : null, enableRangeProcessing: true);
        }
        catch (Exception ex) when (ex is CryptographicException or HttpRequestException or JsonException or OperationCanceledException or FormatException)
        {
            return StatusCode(502, new { error = "media_unavailable" });
        }
    }
}
