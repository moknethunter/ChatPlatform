using System.Text.Json;
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
}
