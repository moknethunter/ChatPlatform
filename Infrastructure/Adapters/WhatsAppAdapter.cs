using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters;

public class WhatsAppAdapter : IIntegrationAdapter
{
    public const string InboundType = "whatsapp.inbound";
    public const string OutboundType = "whatsapp.send";

    public string Name => "whatsapp";

    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<WhatsAppAdapter> _log;

    public WhatsAppAdapter(
        IHttpClientFactory http,
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protectionProvider,
        ILogger<WhatsAppAdapter> log)
    {
        _http = http;
        _scopeFactory = scopeFactory;
        _protector = protectionProvider.CreateProtector("BusinessToken");
        _log = log;
    }

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (evt.Source?.StartsWith("platform.adapters", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (string.Equals(evt.Type, InboundType, StringComparison.OrdinalIgnoreCase))
        {
            var from = Get(evt, "from");
            var text = Get(evt, "text");
            var phoneId = Get(evt, "phoneNumberId");
            if (from is null || text is null || phoneId is null) return;

            await SendMetaMessageAsync(phoneId, from, $"أهلاً بك! وصلتني رسالتك: «{text}»", ct);
            return;
        }

        if (string.Equals(evt.Type, OutboundType, StringComparison.OrdinalIgnoreCase))
        {
            var to = Get(evt, "to");
            var text = Get(evt, "text");
            var phoneId = Get(evt, "phoneNumberId");
            if (to is null || text is null || phoneId is null)
            {
                _log.LogWarning("Outbound event missing 'phoneNumberId'.");
                return;
            }
            await SendMetaMessageAsync(phoneId, to, text, ct);
        }
    }

    private static string? Get(PlatformEvent evt, string key)
        => evt.Payload.TryGetProperty(key, out var v) ? v.GetString() : null;

    private async Task SendMetaMessageAsync(string phoneNumberId, string to, string text, CancellationToken ct)
    {
        string? token = null;
        string? appId = null;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await db.WhatsAppTenants.AsNoTracking()
                .FirstOrDefaultAsync(t => t.PhoneNumberId == phoneNumberId && t.IsActive, ct);

            if (tenant != null && !string.IsNullOrEmpty(tenant.BusinessTokenEncrypted))
            {
                try
                {
                    token = _protector.Unprotect(tenant.BusinessTokenEncrypted);
                    appId = tenant.AppId;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "فشل فك تشفير التوكن للرقم {PhoneId}", phoneNumberId);
                }
            }
        }

        if (string.IsNullOrEmpty(token))
        {
            _log.LogWarning("لا يوجد توكن صالح لـ {PhoneId}", phoneNumberId);
            return;
        }

        var client = _http.CreateClient("whatsapp");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{phoneNumberId}/messages")
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                to = to,
                type = "text",
                text = new { body = text }
            })
        };
        req.Headers.Authorization = new("Bearer", token);

        try
        {
            var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
                _log.LogInformation("WhatsApp sent to {To} (AppId={AppId})", to, appId);
            else
                _log.LogWarning("WhatsApp send failed: {Body}", body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WhatsApp send exception");
        }
    }
}
