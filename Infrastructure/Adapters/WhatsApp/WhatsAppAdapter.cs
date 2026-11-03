using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppAdapter : IIntegrationAdapter
{
    public const string InboundType = "whatsapp.inbound";
    public const string OutboundType = "whatsapp.send";

    public string Name => "whatsapp";

    private readonly IHttpClientFactory _http;
    private readonly IEventBus _bus;
    private readonly PlatformDbContext _db;
    private readonly IDataProtector _protector;
    private readonly WhatsAppOptions _opts;
    private readonly ILogger<WhatsAppAdapter> _log;

    public WhatsAppAdapter(
        IHttpClientFactory http,
        IEventBus bus,
        PlatformDbContext db,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<WhatsAppOptions> opts,
        ILogger<WhatsAppAdapter> log)
    {
        _http = http;
        _bus = bus;
        _db = db;
        _protector = dataProtectionProvider.CreateProtector("BusinessToken");
        _opts = opts.Value;
        _log = log;
    }

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (evt.Source?.StartsWith("platform.adapters", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (string.Equals(evt.Type, InboundType, StringComparison.OrdinalIgnoreCase))
        {
            await HandleInboundAsync(evt, ct);
            return;
        }

        if (string.Equals(evt.Type, OutboundType, StringComparison.OrdinalIgnoreCase))
        {
            await HandleOutboundAsync(evt, ct);
        }
    }

    private async Task HandleInboundAsync(PlatformEvent evt, CancellationToken ct)
    {
        var from = GetString(evt, "from");
        var text = GetString(evt, "text");
        var wamid = GetString(evt, "wamid");

        if (string.IsNullOrWhiteSpace(from)) return;

        _log.LogInformation("WhatsApp inbound from {From}", from);

        if (_opts.AutoReply && !string.IsNullOrWhiteSpace(text))
        {
            var outboundData = new { tenantId = GetString(evt, "tenantId"), phoneNumberId = GetString(evt, "phoneNumberId") };
            var phoneNumberId = outboundData.phoneNumberId ?? _opts.PhoneNumberId;
            var accessToken = await ResolveAccessTokenAsync(outboundData.tenantId, phoneNumberId);
            if (!string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(phoneNumberId))
            {
                await SendTextAsync(phoneNumberId, accessToken, from, "أهلاً بك، تم استلام رسالتك وسيتم الرد عليك قريبًا.", wamid, ct);
            }
        }
    }

    private async Task HandleOutboundAsync(PlatformEvent evt, CancellationToken ct)
    {
        var toRaw = GetString(evt, "to");
        var text = GetString(evt, "text");
        var tenantId = GetString(evt, "tenantId");
        var phoneNumberId = GetString(evt, "phoneNumberId");

        if (string.IsNullOrWhiteSpace(toRaw) || string.IsNullOrWhiteSpace(text))
        {
            _log.LogWarning("whatsapp.send missing 'to' or 'text'");
            return;
        }

        var normalized = NormalizeE164(toRaw);
        if (normalized is null)
        {
            _log.LogWarning("Invalid WhatsApp phone: {Phone}", toRaw);
            await _bus.PublishAsync(new PlatformEvent
            {
                Type = "whatsapp.failed",
                Source = "whatsapp",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    to = toRaw,
                    text,
                    error = "invalid_phone"
                })
            }, ct);
            return;
        }

        var tenant = await ResolveTenantAsync(tenantId, phoneNumberId);
        if (tenant is not null)
        {
            await EnqueueOutboxAsync(tenant, normalized, text, ct, evt.Id.ToString("N"));
            return;
        }

        if (string.IsNullOrWhiteSpace(_opts.PhoneNumberId) || string.IsNullOrWhiteSpace(_opts.AccessToken))
        {
            _log.LogWarning("WhatsApp credentials missing for tenant-specific send");
            await _bus.PublishAsync(new PlatformEvent
            {
                Type = "whatsapp.failed",
                Source = "whatsapp",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    to = normalized,
                    text,
                    error = "missing_credentials"
                })
            }, ct);
            return;
        }

        await SendTextAsync(_opts.PhoneNumberId, _opts.AccessToken, normalized, text, wamid: null, ct);
    }

    private async Task EnqueueOutboxAsync(WhatsAppTenantEntity tenant, string to, string text, CancellationToken ct, string correlationId)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to,
            type = "text",
            text = new { preview_url = false, body = text }
        };

        var item = new WhatsAppOutboxEntity
        {
            TenantId = tenant.TenantId,
            CorrelationId = correlationId,
            IdempotencyKey = $"wa:{tenant.PhoneNumberId}:{to}:{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            PhoneNumberId = tenant.PhoneNumberId,
            RecipientPhone = to,
            MessageType = "text",
            PayloadJson = JsonSerializer.Serialize(payload),
            Status = "Pending",
            NextAttemptAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            MaxAttempts = 10
        };

        _db.WhatsAppOutbox.Add(item);
        await _db.SaveChangesAsync(ct);

        await _bus.PublishAsync(new PlatformEvent
        {
            Type = "whatsapp.outbox.enqueued",
            Source = "whatsapp",
            Payload = JsonSerializer.SerializeToElement(new
            {
                tenantId = tenant.TenantId,
                phoneNumberId = tenant.PhoneNumberId,
                to,
                outboxId = item.Id,
                idempotencyKey = item.IdempotencyKey
            })
        }, ct);
    }

    private async Task<WhatsAppTenantEntity?> ResolveTenantAsync(string? tenantId, string? phoneNumberId)
    {
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            var tenant = await _db.WhatsAppTenants
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive, CancellationToken.None);
            if (tenant is not null && !string.IsNullOrWhiteSpace(tenant.PhoneNumberId) && !string.IsNullOrWhiteSpace(tenant.BusinessTokenEncrypted))
                return tenant;
        }

        if (!string.IsNullOrWhiteSpace(phoneNumberId))
        {
            var tenant = await _db.WhatsAppTenants
                .FirstOrDefaultAsync(x => x.PhoneNumberId == phoneNumberId && x.IsActive, CancellationToken.None);
            if (tenant is not null && !string.IsNullOrWhiteSpace(tenant.BusinessTokenEncrypted))
                return tenant;
        }

        return null;
    }

    private async Task<string?> ResolveAccessTokenAsync(string? tenantId, string? phoneNumberId)
    {
        var tenant = await ResolveTenantAsync(tenantId, phoneNumberId);
        if (tenant is null) return null;

        try
        {
            return _protector.Unprotect(tenant.BusinessTokenEncrypted);
        }
        catch
        {
            _log.LogWarning("Unable to decrypt WhatsApp token for tenant {TenantId}", tenant.TenantId);
            return null;
        }
    }

    private async Task SendTextAsync(string phoneNumberId, string accessToken, string to, string text, string? wamid, CancellationToken ct)
    {
        var client = _http.CreateClient("whatsapp");
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_opts.GraphApiVersion}/{phoneNumberId}/messages")
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to,
                type = "text",
                text = new { preview_url = false, body = text }
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("WhatsApp failed ({Status}): {Body}", (int)res.StatusCode, body);
                await _bus.PublishAsync(new PlatformEvent
                {
                    Type = "whatsapp.failed",
                    Source = "whatsapp",
                    Payload = JsonSerializer.SerializeToElement(new { to, text, error = body, phoneNumberId })
                }, ct);
                return;
            }

            string? messageId = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0)
                {
                    messageId = msgs[0].GetProperty("id").GetString();
                }
            }
            catch { }

            _log.LogInformation("WhatsApp sent to {To}, id={Id}", to, messageId);

            await _bus.PublishAsync(new PlatformEvent
            {
                Type = "whatsapp.sent",
                Source = "whatsapp",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    to,
                    text,
                    messageId,
                    inReplyTo = wamid,
                    phoneNumberId
                })
            }, ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "WhatsApp send exception");
            await _bus.PublishAsync(new PlatformEvent
            {
                Type = "whatsapp.failed",
                Source = "whatsapp",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    to,
                    text,
                    error = ex.Message,
                    phoneNumberId
                })
            }, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "WhatsApp send timeout");
        }
    }

    private static string? GetString(PlatformEvent evt, string key)
        => evt.Payload.ValueKind == JsonValueKind.Object &&
           evt.Payload.TryGetProperty(key, out var v) &&
           v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? NormalizeE164(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var normalized = input.Trim();
        normalized = normalized.Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("(", string.Empty)
            .Replace(")", string.Empty);

        if (normalized.StartsWith("00", StringComparison.Ordinal))
            normalized = normalized[2..];

        if (normalized.StartsWith("+", StringComparison.Ordinal))
            normalized = normalized[1..];

        if (normalized.Length < 8 || normalized.Length > 15)
            return null;

        if (!normalized.All(char.IsDigit))
            return null;

        if (!normalized.StartsWith("0", StringComparison.Ordinal))
            return $"+{normalized}";

        // Some numbers may be local numbers; we do not force a country code here.
        // We keep the output internationalized as E.164 if the caller already provided it,
        // or if it is a local MSISDN without a leading plus sign, we return as-is.
        return $"+{normalized}";
    }
}
