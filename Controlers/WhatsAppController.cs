using ChatPlatform.Infrastructure.Subscriptions;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

    // Verify the exact phone membership in the WABA, including paginated results.
    // Never follow Meta's paging URL: reconstruct the same relative Graph path with
    // an escaped cursor. Bound requests and reject missing or repeated cursors.
    private async Task<bool> VerifyAccount(string phone, string waba, string token, CancellationToken ct)
    {
        if (!ValidMetaId(phone) || !ValidMetaId(waba) || !ValidToken(token)) return false;
        try
        {
            using var client = _httpFactory.CreateClient("whatsapp");
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            const int maxPages = 10;
            for (var page = 0; page < maxPages; page++)
            {
                var path = $"{waba}/phone_numbers?fields=id&limit=100";
                if (cursor != null) path += "&after=" + Uri.EscapeDataString(cursor);
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return false;
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return false;
                if (data.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object &&
                    x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == phone))
                    return true;
                if (!root.TryGetProperty("paging", out var paging) || paging.ValueKind != JsonValueKind.Object ||
                    !paging.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(next.GetString()) ||
                    !paging.TryGetProperty("cursors", out var cursors) || cursors.ValueKind != JsonValueKind.Object ||
                    !cursors.TryGetProperty("after", out var after) || after.ValueKind != JsonValueKind.String)
                    return false;
                cursor = after.GetString();
                if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 4096 ||
                    cursor.Any(char.IsControl) || !seenCursors.Add(cursor))
                    return false;
            }
            return false;
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
    [HttpPost("accounts/{id}/verify")]
    public async Task<IActionResult> VerifySavedAccount(string id, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var account = await _db.WhatsAppTenants.AsNoTracking()
            .Where(x => x.Id == id && x.TenantId == tenant)
            .Select(x => new { x.IsActive, x.PhoneNumberId, x.WabaId, x.BusinessTokenEncrypted })
            .SingleOrDefaultAsync(ct);
        if (account == null) return NotFound();
        if (!account.IsActive || !ValidMetaId(account.WabaId) || string.IsNullOrWhiteSpace(account.BusinessTokenEncrypted))
            return Conflict(new { message = "أعد ربط الحساب أو أكمل بياناته قبل التحقق." });

        string token;
        try { token = _protector.Unprotect(account.BusinessTokenEncrypted); }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return Conflict(new { message = "تعذرت قراءة رمز الوصول المحفوظ. حدّث الرمز ثم أعد التحقق." });
        }

        var valid = await VerifyAccount(account.PhoneNumberId, account.WabaId, token, ct);
        return Ok(new { valid, message = valid
            ? "أكدت Meta أن الرمز المحفوظ يصل إلى هذا الرقم ضمن حساب الأعمال."
            : "تعذر التحقق لدى Meta. راجع الرمز وصلاحياته ومعرّفات الحساب." });
    }

    // Read-only onboarding readiness: distinguish a confirmed subscription from
    // an unknown result. Never send messages, subscribe, or mutate account data here.
    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [HttpPost("accounts/{id}/readiness")]
    public async Task<IActionResult> AccountReadiness(string id, [FromServices] IConfiguration configuration, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var account = await _db.WhatsAppTenants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct);
        if (account == null) return NotFound();
        if (!account.IsActive || !ValidMetaId(account.WabaId) || string.IsNullOrWhiteSpace(account.BusinessTokenEncrypted))
            return Conflict(new { error = "account_incomplete" });
        string token;
        try { token = _protector.Unprotect(account.BusinessTokenEncrypted); }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { return Conflict(new { error = "token_unreadable" }); }

        var appId = configuration["Meta:AppId"] ?? configuration["META_APP_ID"];
        var membership = await VerifyAccount(account.PhoneNumberId, account.WabaId, token, ct);
        var subscribed = membership && ValidMetaId(appId) ? await CheckAppSubscription(account.WabaId, token, appId!, ct) : null;
        return Ok(new { membershipVerified = membership, appIdConfigured = ValidMetaId(appId),
            appSubscribed = subscribed, deliveryVerified = (bool?)null });
    }

    private async Task<bool?> CheckAppSubscription(string wabaId, string token, string appId, CancellationToken ct)
    {
        bool? subscribed = null;
            using var client = _httpFactory.CreateClient("whatsapp");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{wabaId}/subscribed_apps");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    var root = json.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
                        data.ValueKind == JsonValueKind.Array)
                    {
                        var complete = true;
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object ||
                                !item.TryGetProperty("whatsapp_business_api_data", out var details) ||
                                details.ValueKind != JsonValueKind.Object ||
                                !details.TryGetProperty("id", out var app) || app.ValueKind != JsonValueKind.String ||
                                !ValidMetaId(app.GetString()))
                            { complete = false; continue; }
                            if (app.GetString() == appId) { subscribed = true; break; }
                        }
                        // A paginated or malformed response cannot establish absence.
                        if (subscribed == null && complete &&
                            (!root.TryGetProperty("paging", out var paging) ||
                             (paging.ValueKind == JsonValueKind.Object && !paging.TryGetProperty("next", out _))))
                            subscribed = false;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or TaskCanceledException)
            { _logger.LogWarning("Meta subscription check failed: {Kind}", ex.GetType().Name); }
        return subscribed;
    }

    private static bool SignupConfigured(IConfiguration cfg) =>
        cfg.GetValue<bool>("Meta:EmbeddedSignupEnabled") &&
        ValidMetaId(cfg["Meta:AppId"] ?? cfg["META_APP_ID"]) &&
        ValidMetaId(cfg["Meta:EmbeddedSignupConfigId"]) &&
        ValidToken(cfg["Meta:AppSecret"] ?? cfg["META_APP_SECRET"]);

    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("oauth-policy")]
    [HttpPost("embedded-signup/start")]
    public async Task<IActionResult> StartEmbeddedSignup([FromServices] IConfiguration cfg,
        [FromServices] EmbeddedSignupAttemptStore attempts, CancellationToken ct)
    {
        if (!SignupConfigured(cfg)) return Conflict(new { error = "embedded_signup_not_enabled" });
        var tenant = User.TenantId();
        var session = User.FindFirst("platform_session")?.Value;
        if (string.IsNullOrWhiteSpace(session)) return Forbid();
        if (await Entitlements.CapacityAsync(_db, tenant, "accounts", 1, DateTime.UtcNow, ct) is string denied)
            return Conflict(new { error = denied });
        var appId = (cfg["Meta:AppId"] ?? cfg["META_APP_ID"])!;
        var configId = cfg["Meta:EmbeddedSignupConfigId"]!;
        var state = attempts.Start(tenant, session, appId, configId, DateTimeOffset.UtcNow);
        if (state == null) return StatusCode(503, new { error = "signup_busy" });
        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new { appId, configId, state, graphApiVersion = cfg["WhatsApp:GraphApiVersion"] ?? new WhatsAppOptions().GraphApiVersion, expiresInSeconds = 300 });
    }

    // Candidate SDK completion. Never registers phones, subscribes the app or overwrites
    // accounts. The app must already be subscribed and ownership must be proven by Graph.
    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("oauth-policy")]
    [HttpPost("embedded-signup/complete")]
    public async Task<IActionResult> CompleteEmbeddedSignup([FromBody] EmbeddedSignupRequest req,
        [FromServices] IConfiguration cfg, [FromServices] EmbeddedSignupAttemptStore attempts, CancellationToken ct)
    {
        if (!SignupConfigured(cfg)) return Conflict(new { error = "embedded_signup_not_enabled" });
        var tenant = User.TenantId();
        var session = User.FindFirst("platform_session")?.Value;
        if (string.IsNullOrWhiteSpace(session)) return Forbid();
        if (!ValidMetaId(req.PhoneNumberId) || !ValidMetaId(req.WabaId) || !ValidToken(req.Code))
            return BadRequest(new { error = "invalid_signup_data" });
        var appId = (cfg["Meta:AppId"] ?? cfg["META_APP_ID"])!;
        var configId = cfg["Meta:EmbeddedSignupConfigId"]!;
        if (!attempts.Consume(req.State, tenant, session, appId, configId, DateTimeOffset.UtcNow))
            return BadRequest(new { error = "signup_attempt_invalid" });
        Response.Headers["Cache-Control"] = "no-store";
        if (await _db.WhatsAppTenants.AnyAsync(x => x.PhoneNumberId == req.PhoneNumberId, ct))
            return Conflict(new { error = "account_already_registered" });
        if (await Entitlements.CapacityAsync(_db, tenant, "accounts", 1, DateTime.UtcNow, ct) is string denied)
            return Conflict(new { error = denied });

        string? token;
        try
        {
            using var client = _httpFactory.CreateClient("whatsapp-oauth");
            // Meta SDK returns an exchangeable code; no caller-controlled redirect URI.
            // The OAuth client disables HTTP logging and redirects because query parameters are secrets.
            var secret = (cfg["Meta:AppSecret"] ?? cfg["META_APP_SECRET"])!;
            var path = "oauth/access_token?client_id=" + Uri.EscapeDataString(appId)
                + "&client_secret=" + Uri.EscapeDataString(secret) + "&code=" + Uri.EscapeDataString(req.Code);
            using var response = await client.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode) return BadRequest(new { error = "oauth_exchange_failed" });
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            token = doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("access_token", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or TaskCanceledException)
        {
            _logger.LogWarning("Meta signup exchange failed: {Kind}", ex.GetType().Name);
            return BadRequest(new { error = "oauth_exchange_failed" });
        }
        if (!ValidToken(token) || !await VerifyAccount(req.PhoneNumberId, req.WabaId, token!, ct))
            return BadRequest(new { error = "account_verification_failed" });
        if (await CheckAppSubscription(req.WabaId, token!, appId, ct) != true)
            return Conflict(new { error = "app_subscription_not_confirmed" });
        // Reuse the same durable quota, duplicate, tenant and encryption checks as manual linking.
        return await CreateAccount(new CreateTenantRequest(tenant, req.PhoneNumberId, req.WabaId, token!), ct);
    }

    public sealed record EmbeddedSignupRequest(string State, string Code, string WabaId, string PhoneNumberId);

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
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        if (!await _db.Tenants.AnyAsync(x => x.Id == tenant && x.IsActive, ct)) return Forbid();
        if(await Entitlements.CapacityAsync(_db,tenant,"accounts",1,DateTime.UtcNow,ct) is string denied)return Conflict(new{error=denied});
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
        await tx.CommitAsync(ct);
        return Ok(new { id = account.Id, message = "saved" });
    }

    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [HttpPut("accounts/{id}/token")]
    public async Task<IActionResult> UpdateToken(string id, [FromBody] UpdateTokenRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var snapshot = await _db.WhatsAppTenants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct);
        if (snapshot == null) return NotFound();

        // A migrated account may lack WABA metadata. Repair only after Meta confirms
        // membership of the immutable phone ID. Do not replace an established WABA.
        var wabaId = string.IsNullOrWhiteSpace(snapshot.WabaId) ? req.WabaId : snapshot.WabaId;
        if (!string.IsNullOrWhiteSpace(snapshot.WabaId) && req.WabaId != null && req.WabaId != snapshot.WabaId)
            return Conflict(new { error = "waba_change_not_allowed" });
        if (!ValidMetaId(wabaId))
            return BadRequest(new { error = "waba_required" });
        if (!snapshot.IsActive && !req.Reactivate)
            return Conflict(new { error = "account_inactive" });
        if (!await VerifyAccount(snapshot.PhoneNumberId, wabaId!, req.AccessToken, ct))
            return BadRequest(new { error = "account_verification_failed" });

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var account = await _db.WhatsAppTenants.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct);
        if (account == null) return NotFound();
        if (account.PhoneNumberId != snapshot.PhoneNumberId || account.WabaId != snapshot.WabaId ||
            account.IsActive != snapshot.IsActive || account.BusinessTokenEncrypted != snapshot.BusinessTokenEncrypted)
            return Conflict(new { error = "account_changed_retry" });
        if (!account.IsActive)
        {
            if (await Entitlements.CapacityAsync(_db, tenant, "accounts", 1, DateTime.UtcNow, ct) is string denied)
                return Conflict(new { error = denied });
            account.IsActive = true;
        }
        account.WabaId = wabaId!;
        account.BusinessTokenEncrypted = _protector.Protect(req.AccessToken);
        await _db.SaveChangesAsync(ct);
        // Remove deprecated ciphertext so it cannot resurrect a disconnected token.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE WhatsAppTenants SET AccessTokenEncrypted = '' WHERE Id = {id} AND TenantId = {tenant}", ct);
        await tx.CommitAsync(ct);
        return Ok(new { message = "updated" });
    }

    [Authorize(Policy = PlatformSecurity.AdminPolicy)]
    [HttpDelete("accounts/{id}")]
    public async Task<IActionResult> DeleteAccount(string id, CancellationToken ct)
    {
        var tenant = User.TenantId();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var account = await _db.WhatsAppTenants.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct);
        if (account == null) return NotFound();
        // Preserve ownership and history. A disconnected phone must never become claimable by another tenant.
        account.IsActive = false;
        account.BusinessTokenEncrypted = "";
        await _db.SaveChangesAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE WhatsAppTenants SET AccessTokenEncrypted = '' WHERE Id = {id} AND TenantId = {tenant}", ct);
        await tx.CommitAsync(ct);
        return Ok(new { message = "deactivated" });
    }

    // =================================================================
    // 2. التشخيص (Diagnostics)
    // =================================================================

    // GET: api/whatsapp/diagnostics
    [Authorize(Policy = PlatformSecurity.ReadPolicy)]
    [HttpGet("diagnostics")]
    public async Task<IActionResult> GetDiagnostics([FromServices] Microsoft.Extensions.Options.IOptions<WhatsAppOptions> options, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var configured = await _db.WhatsAppTenants.AnyAsync(a => a.TenantId == tenant && a.IsActive &&
            a.BusinessTokenEncrypted != "", ct);
        // Configuration is not live connectivity. Unknown signals must not appear successful.
        return Ok(new
        {
            accountConfigured = configured,
            metaConnected = (bool?)null,
            tokenValid = (bool?)null,
            webhookVerified = (bool?)null,
            webhookSignatureEnabled = !string.IsNullOrWhiteSpace(options.Value.AppSecret),
            inboxWorkerRunning = (bool?)null,
            outboxWorkerRunning = (bool?)null,
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
    string AccessToken, string? WabaId = null, bool Reactivate = false);

public sealed record SendMessageDto(
    string TenantId,
    string To,
    string Text);
