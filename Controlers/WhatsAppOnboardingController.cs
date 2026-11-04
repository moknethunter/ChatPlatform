/*using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/whatsapp")]
public class WhatsAppOnboardingController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IEventBus _bus;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly IDataProtector _protector;
    private readonly ILogger<WhatsAppOnboardingController> _log;

    public WhatsAppOnboardingController(
        PlatformDbContext db,
        IEventBus bus,
        IHttpClientFactory http,
        IConfiguration cfg,
        IDataProtectionProvider protectionProvider,
        ILogger<WhatsAppOnboardingController> log)
    {
        _db = db;
        _bus = bus;
        _http = http;
        _cfg = cfg;
        _protector = protectionProvider.CreateProtector("BusinessToken");
        _log = log;
    }

    [HttpPost("exchange-code")]
    public async Task<IActionResult> ExchangeCode([FromBody] ExchangeReq req)
    {
        var appId = _cfg["Meta:AppId"];
        // السر من متغير البيئة فقط — لا من appsettings
        var appSecret = Environment.GetEnvironmentVariable("META_APP_SECRET") ?? _cfg["Meta:AppSecret"];

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret))
        {
            _log.LogError("Meta AppId أو AppSecret غير مضبوطين");
            return BadRequest("Meta App credentials not configured.");
        }

        var client = _http.CreateClient();
        var url = $"https://graph.facebook.com/v21.0/oauth/access_token" +
                  $"?client_id={appId}&client_secret={appSecret}&code={req.Code}";
        var tokenRes = await client.GetAsync(url);

        if (!tokenRes.IsSuccessStatusCode)
        {
            _log.LogWarning("Meta exchange failed: {Status}", tokenRes.StatusCode);
            return BadRequest("فشل استبدال الكود مع Meta.");
        }

        var tokenJson = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync());
        var businessToken = tokenJson.RootElement.GetProperty("access_token").GetString();

        if (string.IsNullOrEmpty(businessToken))
            return BadRequest("Meta لم تُرجع access token.");

        // 🔐 تشفير التوكن قبل الحفظ في قاعدة البيانات
        var encryptedToken = _protector.Protect(businessToken);

        var existing = await _db.WhatsAppTenants.FirstOrDefaultAsync(t => t.WabaId == req.WabaId);
        if (existing != null)
        {
            existing.BusinessTokenEncrypted = encryptedToken;
            existing.PhoneNumberId = req.PhoneNumberId;
            existing.AppId = appId;
            existing.IsActive = true;
            _db.WhatsAppTenants.Update(existing);
        }
        else
        {
            _db.WhatsAppTenants.Add(new WhatsAppTenantEntity
            {
                WabaId = req.WabaId,
                PhoneNumberId = req.PhoneNumberId,
                BusinessTokenEncrypted = encryptedToken,
                AppId = appId,
                IsActive = true
            });
        }
        await _db.SaveChangesAsync();

        await SubscribeToWebhooks(req.WabaId, businessToken);

        await _bus.PublishAsync(new PlatformEvent
        {
            Type = "whatsapp.tenant.onboarded",
            Source = "whatsapp",
            Payload = JsonSerializer.SerializeToElement(new { req.WabaId, req.PhoneNumberId })
        }, CancellationToken.None);

        _log.LogInformation("تم ربط عميل جديد: WABA {WabaId}", req.WabaId);
        return Ok(new { success = true, message = "تم الربط بنجاح" });
    }

    [HttpPost("register-phone")]
    public async Task<IActionResult> RegisterPhone([FromBody] RegisterReq req)
    {
        var tenant = await _db.WhatsAppTenants
            .FirstOrDefaultAsync(t => t.PhoneNumberId == req.PhoneNumberId && t.IsActive);
        if (tenant == null) return NotFound("العميل غير موجود.");

        string token;
        try
        {
            token = _protector.Unprotect(tenant.BusinessTokenEncrypted);
        }
        catch
        {
            return BadRequest("فشل فك تشفير التوكن.");
        }

        var client = _http.CreateClient("whatsapp");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{req.PhoneNumberId}/register")
        {
            Content = JsonContent.Create(new { messaging_product = "whatsapp", pin = req.Pin })
        };
        request.Headers.Authorization = new("Bearer", token);

        var res = await client.SendAsync(request);
        return res.IsSuccessStatusCode ? Ok() : BadRequest(await res.Content.ReadAsStringAsync());
    }

    private async Task SubscribeToWebhooks(string wabaId, string token)
    {
        var client = _http.CreateClient("whatsapp");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{wabaId}/subscribed_apps");
        request.Headers.Authorization = new("Bearer", token);
        var res = await client.SendAsync(request);
        if (!res.IsSuccessStatusCode)
        
            _log.LogWarning("فشل الاشتراك في Webhooks: {Body}", await res.Content.ReadAsStringAsync());
    }

    public record ExchangeReq(string Code, string WabaId, string PhoneNumberId);
    public record RegisterReq(string PhoneNumberId, string Pin);
    */
using System.Security.Claims;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController, Route("api/whatsapp")]
[EnableRateLimiting("oauth-policy")]
public sealed class WhatsAppOnboardingController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IEventBus _bus;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly IDataProtector _protector;
    private readonly WhatsAppOnboardingService _onboarding;
    private readonly ILogger<WhatsAppOnboardingController> _log;

    public WhatsAppOnboardingController(
        PlatformDbContext db,
        IEventBus bus,
        IHttpClientFactory http,
        IConfiguration cfg,
        IDataProtectionProvider protectionProvider,
        WhatsAppOnboardingService onboarding,
        ILogger<WhatsAppOnboardingController> log)
    {
        _db = db;
        _bus = bus;
        _http = http;
        _cfg = cfg;
        _protector = protectionProvider.CreateProtector("BusinessToken");
        _onboarding = onboarding;
        _log = log;
    }

    // ═════════════════════════════════���═════════════════════
    // 1) Embedded Signup — OAuth exchange + Subscribe + Save
    // ═══════════════════════════════════════════════════════
    [HttpPost("exchange-code")]
    public async Task<IActionResult> ExchangeCode([FromBody] ExchangeReq req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code) ||
            string.IsNullOrWhiteSpace(req.PhoneNumberId) ||
            string.IsNullOrWhiteSpace(req.WabaId))
            return BadRequest(new { error = "missing_fields" });

        // 1) Tenant من الجلسة (أو header مؤقت للتجربة)
        var tenantId = User.FindFirstValue("tenant_id")
                    ?? Request.Headers["X-Tenant-Id"].FirstOrDefault()
                    ?? "default";

        var actor = User.Identity?.Name ?? "anonymous";

        // 2) استبدال code بـ token عبر WhatsAppOnboardingService
        var exchange = await _onboarding.ExchangeCodeAsync(req.Code, req.RedirectUri, ct);
        if (!exchange.Success)
        {
            _log.LogWarning("OAuth failed for actor={Actor}: {Code} {Msg}",
                actor, exchange.ErrorCode, exchange.ErrorMessage);
            return BadRequest(new { error = exchange.ErrorCode, message = exchange.ErrorMessage });
        }

        // 3) التحقق من ملكية WABA (لا نثق بـ req.WabaId)
        var waba = await _onboarding.GetWabaDetailsAsync(req.WabaId, exchange.AccessToken!, ct);
        if (!waba.Success)
        {
            _log.LogWarning("WABA verification failed for actor={Actor}, wabaId={WabaId}: {Err}",
                actor, req.WabaId, waba.ErrorCode);
            return BadRequest(new { error = "waba_verification_failed", message = "Cannot verify WABA ownership" });
        }

        // 4) حفظ Tenant (بعد التحقق فقط)
        var save = await _onboarding.SaveTenantAsync(
            _db, tenantId, req.PhoneNumberId, req.WabaId, exchange.AccessToken!, ct);
        if (!save.Success)
            return BadRequest(new { error = "save_failed", message = save.Error });

        // 5) الاشتراك في Webhooks — إجباري
        var subscribed = await SubscribeToWebhooksAsync(req.WabaId, exchange.AccessToken!, ct);
        if (!subscribed.Success)
        {
            _log.LogError("Webhook subscription failed for wabaId={WabaId}: {Body}",
                req.WabaId, subscribed.Error);
            return StatusCode(500, new
            {
                error = "webhook_subscription_failed",
                message = "Tenant saved but webhooks not active. Contact support."
            });
        }

        // 6) بث حدث Onboarded
        await _bus.PublishAsync(new PlatformEvent
        {
            Type = "whatsapp.tenant.onboarded",
            Source = "whatsapp",
            Payload = JsonSerializer.SerializeToElement(new
            {
                tenantId,
                req.WabaId,
                req.PhoneNumberId
            })
        }, ct);

        _log.LogInformation(
            "Onboarded: actor={Actor}, tenant={Tenant}, phoneId={PhoneId}, wabaId={WabaId}, ip={Ip}",
            actor, tenantId, req.PhoneNumberId, req.WabaId,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return Ok(new
        {
            success = true,
            phoneNumberId = save.PhoneNumberId,
            tenantId,
            wabaDetails = new
            {
                waba.Name,
                waba.AccountReviewStatus,
                waba.BusinessVerificationStatus,
                waba.Status
            }
        });
    }

    // ═══════════════════════════════════════════════════════
    // 2) Register phone with PIN
    // ═══════════════════════════════════════════════════════
    [HttpPost("register-phone")]
    public async Task<IActionResult> RegisterPhone([FromBody] RegisterReq req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.PhoneNumberId) || string.IsNullOrWhiteSpace(req.Pin))
            return BadRequest(new { error = "missing_fields" });

        var tenant = await _db.WhatsAppTenants
            .FirstOrDefaultAsync(t => t.PhoneNumberId == req.PhoneNumberId && t.IsActive, ct);
        if (tenant == null) return NotFound(new { error = "tenant_not_found" });

        string token;
        try { token = _protector.Unprotect(tenant.BusinessTokenEncrypted); }
        catch
        {
            _log.LogError("Token decrypt failed for phoneId={PhoneId}", req.PhoneNumberId);
            return BadRequest(new { error = "token_decrypt_failed" });
        }

        var client = _http.CreateClient("whatsapp");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{req.PhoneNumberId}/register")
        {
            Content = JsonContent.Create(new { messaging_product = "whatsapp", pin = req.Pin })
        };
        request.Headers.Authorization = new("Bearer", token);

        var res = await client.SendAsync(request, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("Register phone failed for {PhoneId}: {Body}",
                req.PhoneNumberId, body);
            return BadRequest(new { error = "register_failed", message = body });
        }

        _log.LogInformation("Phone {PhoneId} registered with Meta", req.PhoneNumberId);
        return Ok(new { success = true });
    }

    // ═══════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════
    private async Task<(bool Success, string? Error)> SubscribeToWebhooksAsync(
        string wabaId, string token, CancellationToken ct)
    {
        var client = _http.CreateClient("whatsapp");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{wabaId}/subscribed_apps");
        request.Headers.Authorization = new("Bearer", token);

        try
        {
            var res = await client.SendAsync(request, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            return res.IsSuccessStatusCode
                ? (true, null)
                : (false, body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public record ExchangeReq(string Code, string WabaId, string PhoneNumberId, string? RedirectUri);
    public record RegisterReq(string PhoneNumberId, string Pin);
}
