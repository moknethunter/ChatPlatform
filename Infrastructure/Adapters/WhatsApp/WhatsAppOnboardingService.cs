using System.Text.Json;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    private readonly WhatsAppOptions _options;

    public WhatsAppOnboardingService(
        IHttpClientFactory httpFactory,
        IDataProtectionProvider protection,
        IConfiguration cfg,
        ILogger<WhatsAppOnboardingService> log,
        IOptions<WhatsAppOptions> options)
    {
        _httpFactory = httpFactory;
        _protector = protection.CreateProtector("BusinessToken");
        _cfg = cfg;
        _log = log;
        _options = options.Value;
    }

    /// <summary>
    /// الخطوة 1: استبدال OAuth code بـ access_token دائم.
    /// يُستدعى من endpoint /api/whatsapp/exchange-code بعد Embedded Signup.
    /// </summary>
    public async Task<ExchangeTokenResult> ExchangeCodeAsync(
        string code,
        string? redirectUri,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return ExchangeTokenResult.Fail(
                "invalid_code",
                "code is required");

        var appId = _cfg["Meta:AppId"];

        var appSecret =
            Environment.GetEnvironmentVariable("META_APP_SECRET")
            ?? _cfg["Meta:AppSecret"];

        if (string.IsNullOrWhiteSpace(appId)
            || string.IsNullOrWhiteSpace(appSecret))
        {
            _log.LogError(
                "Meta AppId/AppSecret not configured");

            return ExchangeTokenResult.Fail(
                "not_configured",
                "Meta AppId/AppSecret missing. Set META_APP_SECRET env var.");
        }

        var client = _httpFactory.CreateClient("whatsapp");

        // OAuth endpoint بدون إصدار داخل الرابط.
        var oauthUri =
            new Uri("https://graph.facebook.com/oauth/access_token");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = appId,
            ["client_secret"] = appSecret,
            ["code"] = code
        };

        if (!string.IsNullOrWhiteSpace(redirectUri))
            form["redirect_uri"] = redirectUri;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            oauthUri)
        {
            Content = new FormUrlEncodedContent(form)
        };

        try
        {
            using var response =
                await client.SendAsync(request, ct);

            var body =
                await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "OAuth exchange failed ({Status}): {Body}",
                    (int)response.StatusCode,
                    Truncate(body, 500));

                return ExchangeTokenResult.Fail(
                    $"http_{(int)response.StatusCode}",
                    "OAuth exchange rejected by Meta");
            }

            using var document =
                JsonDocument.Parse(body);

            var root = document.RootElement;

            var accessToken =
                root.TryGetProperty(
                    "access_token",
                    out var tokenElement)
                    ? tokenElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return ExchangeTokenResult.Fail(
                    "no_token",
                    "Meta returned no access_token");
            }

            var expiresIn =
                root.TryGetProperty(
                    "expires_in",
                    out var expiresElement)
                && expiresElement.TryGetInt32(
                    out var seconds)
                    ? seconds
                    : (int?)null;

            var tokenType =
                root.TryGetProperty(
                    "token_type",
                    out var tokenTypeElement)
                    ? tokenTypeElement.GetString()
                    : "bearer";

            return ExchangeTokenResult.Ok(
                accessToken,
                tokenType,
                expiresIn);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "OAuth exchange exception");

            return ExchangeTokenResult.Fail(
                "network_error",
                ex.Message);
        }
    }

    /// <summary>
    /// الخطوة 2: حفظ Tenant كامل.
    /// التوكن يتم تشفيره قبل الحفظ.
    /// </summary>
    public async Task<TenantSaveResult> SaveTenantAsync(
        PlatformDbContext db,
        string tenantId,
        string phoneNumberId,
        string wabaId,
        string accessToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId)
            || string.IsNullOrWhiteSpace(wabaId)
            || string.IsNullOrWhiteSpace(accessToken))
        {
            return TenantSaveResult.Fail("missing_fields");
        }

        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId && t.IsActive, ct))
            return TenantSaveResult.Fail("invalid_tenant");

        var encrypted =
            _protector.Protect(accessToken);

        var existing =
            await db.WhatsAppTenants.FirstOrDefaultAsync(
                x => x.PhoneNumberId == phoneNumberId,
                ct);

        if (existing != null)
        {
            // Never silently transfer an account to another organization.
            if (!string.Equals(existing.TenantId, tenantId, StringComparison.Ordinal))
                return TenantSaveResult.Fail("account_already_registered");
            existing.WabaId = wabaId;
            existing.BusinessTokenEncrypted = encrypted;
            existing.IsActive = true;
        }
        else
        {
            db.WhatsAppTenants.Add(
                new WhatsAppTenantEntity
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
            phoneNumberId,
            wabaId,
            tenantId);

        return TenantSaveResult.Ok(phoneNumberId);
    }

    /// <summary>
    /// الخطوة 3: جلب تفاصيل الحساب للتحقق من حالة WABA.
    /// </summary>
    public async Task<WabaDetailsResult> GetWabaDetailsAsync(
        string wabaId,
        string accessToken,
        CancellationToken ct)
    {
        var client =
            _httpFactory.CreateClient("whatsapp");

        var uri = new Uri(
            $"https://graph.facebook.com/" +
            $"{_options.GraphApiVersion.Trim('/')}/" +
            $"{wabaId}" +
            "?fields=id,name,account_review_status," +
            "business_verification_status,status");

        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                uri);

        request.Headers.Authorization =
            new System.Net.Http.Headers
                .AuthenticationHeaderValue(
                    "Bearer",
                    accessToken);

        try
        {
            using var response =
                await client.SendAsync(request, ct);

            var body =
                await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return WabaDetailsResult.Fail(
                    $"http_{(int)response.StatusCode}",
                    Truncate(body, 500));
            }

            using var document =
                JsonDocument.Parse(body);

            var root = document.RootElement;

            return WabaDetailsResult.Ok(
                id: root.TryGetProperty(
                    "id",
                    out var id)
                    ? id.GetString()
                    : null,

                name: root.TryGetProperty(
                    "name",
                    out var name)
                    ? name.GetString()
                    : null,

                accountReviewStatus:
                    root.TryGetProperty(
                        "account_review_status",
                        out var review)
                        ? review.GetString()
                        : null,

                businessVerificationStatus:
                    root.TryGetProperty(
                        "business_verification_status",
                        out var verification)
                        ? verification.GetString()
                        : null,

                status: root.TryGetProperty(
                    "status",
                    out var status)
                    ? status.GetString()
                    : null);
        }
        catch (Exception ex)
        {
            return WabaDetailsResult.Fail(
                "network_error",
                ex.Message);
        }
    }

    private static string Truncate(
        string value,
        int max)
    {
        return value.Length <= max
            ? value
            : value[..max] + "…";
    }
}

// ============================================================
// النتائج المهيكلة
// ============================================================

public sealed record ExchangeTokenResult(
    bool Success,
    string? AccessToken,
    string? TokenType,
    int? ExpiresInSeconds,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ExchangeTokenResult Ok(
        string token,
        string? type,
        int? expires)
    {
        return new ExchangeTokenResult(
            true,
            token,
            type,
            expires,
            null,
            null);
    }

    public static ExchangeTokenResult Fail(
        string code,
        string message)
    {
        return new ExchangeTokenResult(
            false,
            null,
            null,
            null,
            code,
            message);
    }
}

public sealed record TenantSaveResult(
    bool Success,
    string? PhoneNumberId,
    string? Error)
{
    public static TenantSaveResult Ok(
        string phoneNumberId)
    {
        return new TenantSaveResult(
            true,
            phoneNumberId,
            null);
    }

    public static TenantSaveResult Fail(
        string error)
    {
        return new TenantSaveResult(
            false,
            null,
            error);
    }
}

public sealed record WabaDetailsResult(
    bool Success,
    string? Id,
    string? Name,
    string? AccountReviewStatus,
    string? BusinessVerificationStatus,
    string? Status,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static WabaDetailsResult Ok(
        string? id,
        string? name,
        string? accountReviewStatus,
        string? businessVerificationStatus,
        string? status)
    {
        return new WabaDetailsResult(
            true,
            id,
            name,
            accountReviewStatus,
            businessVerificationStatus,
            status,
            null,
            null);
    }

    public static WabaDetailsResult Fail(
        string code,
        string message)
    {
        return new WabaDetailsResult(
            false,
            null,
            null,
            null,
            null,
            null,
            code,
            message);
    }
}
