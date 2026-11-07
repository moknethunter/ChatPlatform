using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>
/// خدمة Embedded Signup / Onboarding — الربط التلقائي للعملاء.
/// لا ترسل رسائل — هذا عمل WhatsAppCloudClient + WhatsAppOutboxWorker.
/// مسؤولياتها: استبدال code بتوكن دائم، حفظ Tenant، التحقق من الحساب.
/// </summary>
public sealed class WhatsAppOnboardingService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<WhatsAppOnboardingService> _log;
    private readonly IConfiguration _cfg;

    public WhatsAppOnboardingService(
        IHttpClientFactory httpFactory,
        IDataProtectionProvider protection,
        IConfiguration cfg,
        ILogger<WhatsAppOnboardingService> log)
    {
        _httpFactory = httpFactory;
        _protector = protection.CreateProtector("BusinessToken");
        _cfg = cfg;
        _log = log;
    }

    /// <summary>
    /// الخطوة 1: استبدال OAuth code بـ access_token دائم.
    /// يُستدعى من endpoint /api/whatsapp/exchange-code بعد Embedded Signup.
    /// </summary>
    public async Task<ExchangeTokenResult> ExchangeCodeAsync(
        string code, string? redirectUri, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return ExchangeTokenResult.Fail("invalid_code", "code is required");

        var appId = _cfg["Meta:AppId"];
        var appSecret = Environment.GetEnvironmentVariable("META_APP_SECRET")
                        ?? _cfg["Meta:AppSecret"];

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            _log.LogError("Meta AppId/AppSecret not configured");
            return ExchangeTokenResult.Fail("not_configured",
                "Meta AppId/AppSecret missing. Set META_APP_SECRET env var.");
        }

        var client = _httpFactory.CreateClient("whatsapp");
        // OAuth endpoint على graph.facebook.com (بدون version)
        var oauthUri = new Uri("https://graph.facebook.com/oauth/access_token");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = appId,
            ["client_secret"] = appSecret,
            ["code"] = code
        };
        if (!string.IsNullOrWhiteSpace(redirectUri))
            form["redirect_uri"] = redirectUri;

        using var req = new HttpRequestMessage(HttpMethod.Post, oauthUri)
        {
            Content = new FormUrlEncodedContent(form)
        };

        try
        {
            using var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("OAuth exchange failed ({Status}): {Body}",
                    (int)res.StatusCode, Truncate(body, 500));
                return ExchangeTokenResult.Fail(
                    $"http_{(int)res.StatusCode}", "OAuth exchange rejected by Meta");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                return ExchangeTokenResult.Fail("no_token", "Meta returned no access_token");

            var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s)
                ? s : (int?)null;
            var tokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : "bearer";

            return ExchangeTokenResult.Ok(accessToken, tokenType, expiresIn);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OAuth exchange exception");
            return ExchangeTokenResult.Fail("network_error", ex.Message);
        }
    }

    /// <summary>
    /// الخطوة 2: حفظ Tenant كامل (توكن مشفر + phone_number_id + waba_id).
    /// يُستدعى بعد ExchangeCodeAsync.
    /// </summary>
    public async Task<TenantSaveResult> SaveTenantAsync(
        PlatformDbContext db,
        string tenantId,
        string phoneNumberId,
        string wabaId,
        string accessToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId) ||
            string.IsNullOrWhiteSpace(wabaId) ||
            string.IsNullOrWhiteSpace(accessToken))
            return TenantSaveResult.Fail("missing_fields");

        var encrypted = _protector.Protect(accessToken);

        var existing = await db.WhatsAppTenants
            .FirstOrDefaultAsync(x => x.PhoneNumberId == phoneNumberId, ct);

        if (existing != null)
        {
            existing.TenantId = tenantId;
            existing.WabaId = wabaId;
            existing.BusinessTokenEncrypted = encrypted;
            existing.IsActive = true;
        }
        else
        {
            db.WhatsAppTenants.Add(new WhatsAppTenantEntity
            {
                TenantId = tenantId,
                PhoneNumberId = phoneNumberId,
                WabaId = wabaId,
                BusinessTokenEncrypted = encrypted,
                IsActive = true
            });
        }

        await db.SaveChangesAsync(ct);
        _log.LogInformation(
            "Tenant saved: phoneId={PhoneId}, wabaId={WabaId}, tenant={Tenant}",
            phoneNumberId, wabaId, tenantId);

        return TenantSaveResult.Ok(phoneNumberId);
    }

    /// <summary>
    /// الخطوة 3 (اختياري): جلب تفاصيل الحساب — للتحقق من حالة WABA.
    /// </summary>
    public async Task<WabaDetailsResult> GetWabaDetailsAsync(
        string wabaId, string accessToken, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("whatsapp");
        var uri = new Uri(
            $"https://graph.facebook.com/v21.0/{wabaId}?fields=id,name,account_review_status,business_verification_status,status");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return WabaDetailsResult.Fail($"http_{(int)res.StatusCode}", Truncate(body, 500));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return WabaDetailsResult.Ok(
                id: root.TryGetProperty("id", out var id) ? id.GetString() : null,
                name: root.TryGetProperty("name", out var n) ? n.GetString() : null,
                accountReviewStatus: root.TryGetProperty("account_review_status", out var ar) ? ar.GetString() : null,
                businessVerificationStatus: root.TryGetProperty("business_verification_status", out var bv) ? bv.GetString() : null,
                status: root.TryGetProperty("status", out var st) ? st.GetString() : null);
        }
        catch (Exception ex)
        {
            return WabaDetailsResult.Fail("network_error", ex.Message);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

// ═══════════════════════════════════════════════════════════
//  النتائج المهيكلة
// ═══════════════════════════════════════════════════════════
public sealed record ExchangeTokenResult(
    bool Success, string? AccessToken, string? TokenType,
    int? ExpiresInSeconds, string? ErrorCode, string? ErrorMessage)
{
    public static ExchangeTokenResult Ok(string token, string? type, int? expires) =>
        new(true, token, type, expires, null, null);
    public static ExchangeTokenResult Fail(string code, string message) =>
        new(false, null, null, null, code, message);
}

public sealed record TenantSaveResult(bool Success, string? PhoneNumberId, string? Error)
{
    public static TenantSaveResult Ok(string phoneNumberId) => new(true, phoneNumberId, null);
    public static TenantSaveResult Fail(string error) => new(false, null, error);
}

public sealed record WabaDetailsResult(
    bool Success, string? Id, string? Name, string? AccountReviewStatus,
    string? BusinessVerificationStatus, string? Status,
    string? ErrorCode, string? ErrorMessage)
{
    public static WabaDetailsResult Ok(string? id, string? name, string? accountReviewStatus, string? businessVerificationStatus, string? status) =>
        new(true, id, name, accountReviewStatus, businessVerificationStatus, status, null, null);
    public static WabaDetailsResult Fail(string code, string message) =>
        new(false, null, null, null, null, null, code, message);
}
