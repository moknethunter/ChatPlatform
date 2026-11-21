using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatPlatform.Infrastructure.Adapters.WhatsApp.Binding;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/whatsapp")]
public sealed class WhatsAppController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IWhatsAppTenantResolver _tenantResolver;
    private readonly WhatsAppTokenProtection _protector;
    private readonly ILogger<WhatsAppController> _logger;

    public WhatsAppController(
        PlatformDbContext db,
        IHttpClientFactory httpFactory,
        IWhatsAppTenantResolver tenantResolver,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<WhatsAppController> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _tenantResolver = tenantResolver;
        _protector = new WhatsAppTokenProtection(dataProtectionProvider);
        _logger = logger;
    }

    // =================================================================
    // 1. إدارة الحسابات (Accounts Management)
    // =================================================================

    // GET: api/whatsapp/accounts
    [Authorize(Policy = PlatformSecurity.ReadPolicy)]
    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts(CancellationToken ct)
    {
        var tenant = User.TenantId();
        var accounts = await _db.WhatsAppTenants
            .AsNoTracking().Where(a => a.TenantId == tenant)
            .Select(a => new
            {
                Id = a.Id,
                TenantId = a.TenantId,
                PhoneNumberId = a.PhoneNumberId,
                WabaId = a.WabaId,
                DisplayName = a.PhoneNumberId, // يمكنك تعديله لاحقاً لجلب اسم الحساب
                IsActive = a.IsActive,
                HasToken = !string.IsNullOrEmpty(a.BusinessTokenEncrypted)
            })
            .ToListAsync(ct);

        return Ok(accounts);
    }

    private static bool ValidMetaId(string? id) => id is { Length: >= 1 and <= 32 } && id.All(c => c is >= '0' and <= '9');
    private static bool ValidToken(string? token) => token is { Length: >= 1 and <= 4096 } && !token.Any(char.IsWhiteSpace);

    // A successful phone lookup alone does not establish membership in the supplied WABA.
    // Fail closed if the phone is not on the first returned page; never follow arbitrary paging URLs.
    private async Task<bool> VerifyAccount(string phone, string waba, string token, CancellationToken ct)
    {
        if (!ValidMetaId(phone) || !ValidMetaId(waba) || !ValidToken(token)) return false;
        try
        {
            var client = _httpFactory.CreateClient("whatsapp");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{waba}/phone_numbers?fields=id&limit=100");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                && data.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object &&
                    x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == phone);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or TaskCanceledException)
        {
            _logger.LogWarning("Meta account verification failed: {Kind}", ex.GetType().Name);
            return false;
        }
    }

    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [HttpPost("validate-credentials")]
    public async Task<IActionResult> ValidateCredentials([FromBody] ValidateMetaRequest req, CancellationToken ct)
    {
        var valid = await VerifyAccount(req.PhoneNumberId, req.WabaId, req.AccessToken, ct);
        return Ok(new { valid, message = valid ? "تم التحقق من ملكية الرقم ضمن WABA." : "تعذر التحقق من البيانات." });
    }

    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [HttpPost("accounts")]
    public async Task<IActionResult> CreateAccount([FromBody] CreateTenantRequest dto, CancellationToken ct)
    {
        var tenant = User.TenantId();
        if (dto.TenantId != tenant) return Forbid();
        if (!ValidMetaId(dto.PhoneNumberId) || !ValidMetaId(dto.WabaId) || !ValidToken(dto.AccessToken))
            return BadRequest(new { error = "invalid_credentials" });
        if (await _db.WhatsAppTenants.AnyAsync(x => x.PhoneNumberId == dto.PhoneNumberId, ct))
            return Conflict(new { error = "account_already_registered" });
        if (!await VerifyAccount(dto.PhoneNumberId, dto.WabaId, dto.AccessToken, ct))
            return BadRequest(new { error = "account_verification_failed" });
        var account = new WhatsAppTenantEntity { TenantId = tenant, PhoneNumberId = dto.PhoneNumberId,
            WabaId = dto.WabaId, BusinessTokenEncrypted = _protector.Protect(dto.AccessToken), IsActive = true };
        _db.WhatsAppTenants.Add(account);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            if (await _db.WhatsAppTenants.AnyAsync(x => x.PhoneNumberId == dto.PhoneNumberId, ct))
                return Conflict(new { error = "account_already_registered" });
            throw;
        }
        return Ok(new { id = account.Id, message = "saved" });
    }

    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [HttpPut("accounts/{id}/token")]
    public async Task<IActionResult> UpdateToken(string id, [FromBody] UpdateTokenRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var account = await _db.WhatsAppTenants.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct);
        if (account == null) return NotFound();
        if (!await VerifyAccount(account.PhoneNumberId, account.WabaId, req.AccessToken, ct))
            return BadRequest(new { error = "account_verification_failed" });
        account.BusinessTokenEncrypted = _protector.Protect(req.AccessToken);
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "updated" });
    }

    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [HttpDelete("accounts/{id}")]
    public async Task<IActionResult> DeleteAccount(string id, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var account = await _db.WhatsAppTenants.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct);
        if (account == null) return NotFound();
        // Preserve ownership and history. A disconnected phone must never become claimable by another tenant.
        account.IsActive = false;
        account.BusinessTokenEncrypted = "";
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "deactivated" });
    }

    // =================================================================
    // 2. التشخيص (Diagnostics)
    // =================================================================

    // GET: api/whatsapp/diagnostics
    [HttpGet("diagnostics")]
    public IActionResult GetDiagnostics()
    {
        // في بيئة إنتاج، يجب فحص حالة الـ Workers فعلياً من الـ DI
        return Ok(new
        {
            metaConnected = true, // يمكن فحصها بطلب بسيط لميتا
            tokenValid = true,
            webhookVerified = true,
            webhookSignatureEnabled = true,
            inboxWorkerRunning = true,
            outboxWorkerRunning = true,
            webhookUrl = $"{Request.Scheme}://{Request.Host}/webhook"
        });
    }

    // =================================================================
    // 3. الرسائل (Messaging & Inbox)
    // =================================================================

    // GET: api/whatsapp/messages
    [HttpGet("messages")]
    public async Task<IActionResult> GetMessages(
        [FromQuery] string? tenantId,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) tenantId = "default";
        var safeTake = Math.Clamp(take, 1, 200);

        var items = await _db.WhatsAppMessages.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderByDescending(m => m.TimestampUtc)
            .Take(safeTake)
            .Select(m => new
            {
                id = m.Id,
                direction = m.Direction,
                messageType = m.MessageType,
                textPreview = m.TextPreview,
                status = m.Status,
                timestampUtc = m.TimestampUtc,
                wamid = m.Wamid
            })
            .ToListAsync(ct);

        return Ok(new { ok = true, messages = items });
    }

    // POST: api/whatsapp/send
    [HttpPost("send")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.TenantId) || 
            string.IsNullOrWhiteSpace(dto.To) || 
            string.IsNullOrWhiteSpace(dto.Text))
            return BadRequest(new { message = "TenantId, To and Text are required" });

        var account = await _db.WhatsAppTenants
            .FirstOrDefaultAsync(x => x.TenantId == dto.TenantId && x.IsActive, ct);
            
        if (account == null)
            return BadRequest(new { message = "WhatsApp account is not connected." });

        // فك تشفير التوكن لاستخدامه في الإرسال
        string decryptedToken;
        try
        {
            decryptedToken = _protector.Unprotect(account.BusinessTokenEncrypted);
        }
        catch
        {
            return BadRequest(new { message = "فشل فك تشفير التوكن. يرجى تحديثه." });
        }

        var client = _httpFactory.CreateClient("whatsapp");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", decryptedToken);

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = dto.To,
            type = "text",
            text = new { preview_url = false, body = dto.Text }
        };

        var response = await client.PostAsJsonAsync($"{account.PhoneNumberId}/messages", payload, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("WhatsApp send failed: {Status} {Body}", response.StatusCode, responseBody);
            return BadRequest(new { message = "Send failed", detail = responseBody });
        }

        // حفظ الرسالة في قاعدة البيانات
        var conversation = await _db.WhatsAppConversations.FirstOrDefaultAsync(c =>
            c.TenantId == dto.TenantId && c.PhoneNumberId == account.PhoneNumberId && c.ContactPhone == dto.To, ct);

        if (conversation == null)
        {
            conversation = new WhatsAppConversationEntity
            {
                TenantId = dto.TenantId,
                PhoneNumberId = account.PhoneNumberId,
                ContactPhone = dto.To,
                Status = "open",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _db.WhatsAppConversations.Add(conversation);
            await _db.SaveChangesAsync(ct);
        }

        _db.WhatsAppMessages.Add(new WhatsAppMessageEntity
        {
            TenantId = dto.TenantId,
            ConversationId = conversation.Id,
            Direction = "Outbound",
            MessageType = "text",
            ContentJson = JsonSerializer.Serialize(payload),
            TextPreview = dto.Text,
            Status = "Sent",
            TimestampUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "Message sent successfully" });
    }

    // =================================================================
    // 4. الحصول على قائمة المؤسسات (للقائمة المنسدلة في الواجهة)
    // =================================================================
    
    // GET: api/whatsapp/tenants
    [HttpGet("tenant-options")]
    public async Task<IActionResult> GetTenants(CancellationToken ct)
    {
        // افترض أن لديك جدول للـ Tenants، أو استخدم WhatsAppTenants كبديل مؤقت
        var tenants = await _db.WhatsAppTenants
            .AsNoTracking()
            .Select(t => new { Id = t.TenantId, Name = t.TenantId })
            .Distinct()
            .ToListAsync(ct);

        // إذا لم توجد مؤسسات، أرجع مؤسسة افتراضية للتجربة
        if (!tenants.Any())
            tenants.Add(new { Id = "tenant-1", Name = "المؤسسة الافتراضية" });

        return Ok(tenants);
    }
}

// =================================================================
// DTOs (Data Transfer Objects)
// =================================================================

public sealed record ValidateMetaRequest(
    string PhoneNumberId,
    string WabaId,
    string AccessToken);

public sealed record CreateTenantRequest(
    string TenantId,
    string PhoneNumberId,
    string WabaId,
    string AccessToken);

public sealed record UpdateTokenRequest(
    string AccessToken);

public sealed record SendMessageDto(
    string TenantId,
    string To,
    string Text);
