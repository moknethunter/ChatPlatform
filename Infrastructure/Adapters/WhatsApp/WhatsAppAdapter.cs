/*using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using Microsoft.Extensions.Options;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppAdapter : IIntegrationAdapter
{
    public const string InboundType = "whatsapp.inbound";
    public const string OutboundType = "whatsapp.send";

    public string Name => "whatsapp";

    private readonly IHttpClientFactory _http;
    private readonly IEventBus _bus;
    private readonly WhatsAppOptions _opts;
    private readonly ILogger<WhatsAppAdapter> _log;

    public WhatsAppAdapter(
        IHttpClientFactory http,
        IEventBus bus,
        IOptions<WhatsAppOptions> opts,
        ILogger<WhatsAppAdapter> log)
    {
        _http = http;
        _bus = bus;
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
            await SendTextAsync(from, "أهلاً بك، تم استلام رسالتك وسيتم الرد عليك قريبًا.", wamid, ct);
        }
    }

    private async Task HandleOutboundAsync(PlatformEvent evt, CancellationToken ct)
    {
        var toRaw = GetString(evt, "to");
        var text = GetString(evt, "text");

        if (string.IsNullOrWhiteSpace(toRaw) || string.IsNullOrWhiteSpace(text))
        {
            _log.LogWarning("whatsapp.send missing 'to' or 'text'");
            return;
        }

        var to = CleanYemeniNumber(toRaw);
        if (to is null)
        {
            _log.LogWarning("Invalid Yemeni phone: {Phone}", toRaw);
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

        await SendTextAsync(to, text, wamid: null, ct);
    }

    private async Task SendTextAsync(string to, string text, string? wamid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.PhoneNumberId) || string.IsNullOrWhiteSpace(_opts.AccessToken))
        {
            _log.LogWarning("WhatsApp credentials missing");
            return;
        }

        var client = _http.CreateClient("whatsapp");
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_opts.GraphApiVersion}/{_opts.PhoneNumberId}/messages")
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
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.AccessToken);

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
                    Payload = JsonSerializer.SerializeToElement(new { to, text, error = body })
                }, ct);
                return;
            }

            string? messageId = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("messages", out var msgs) &&
                    msgs.GetArrayLength() > 0)
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
                    inReplyTo = wamid
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
                    error = ex.Message
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
            



    private static string? CleanYemeniNumber(string input)
   
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var p = input.Replace("+", "").Replace(" ", "").Replace("-", "").Trim();
        if (p.StartsWith("00")) p = p[2..];
        if (p.StartsWith("0")) p = "967" + p[1..];
        if (!p.StartsWith("967")) p = "967" + p;
        return Regex.IsMatch(p, @"^9677[0-8]\d{7}$") ? p : null;
    }
}
*/

using System.Text.Json;
using System.Text.RegularExpressions;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>
/// محول WhatsApp الصادر. يُنشئ Message + Outbox معاً.
/// الإرسال الفعلي عبر WhatsAppOutboxWorker.
/// </summary>
public sealed class WhatsAppAdapter : IIntegrationAdapter
{
    public const string OutboundType = "whatsapp.send";
    public string Name => "whatsapp";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly WhatsAppOptions _opts;
    private readonly ILogger<WhatsAppAdapter> _log;

    public WhatsAppAdapter(
        IServiceScopeFactory scopeFactory,
        IEventBus bus,
        IOptions<WhatsAppOptions> opts,
        ILogger<WhatsAppAdapter> log)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _opts = opts.Value;
        _log = log;
    }

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (evt.Source?.StartsWith("platform.adapters", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (string.Equals(evt.Type, OutboundType, StringComparison.OrdinalIgnoreCase))
            await HandleOutboundAsync(evt, ct);
    }

    private async Task HandleOutboundAsync(PlatformEvent evt, CancellationToken ct)
    {
        var phoneId = GetString(evt, "phoneNumberId") ?? _opts.PhoneNumberId;
        var toRaw = GetString(evt, "to");
        var text = GetString(evt, "text");

        if (string.IsNullOrWhiteSpace(phoneId) || string.IsNullOrWhiteSpace(toRaw))
        {
            _log.LogWarning("whatsapp.send {EvtId}: missing phoneNumberId or to", evt.Id);
            await PublishFailedAsync(toRaw, "missing_fields", ct);
            return;
        }

        var to = NormalizePhone(toRaw);
        if (to is null)
        {
            _log.LogWarning("Invalid phone: {Phone}", toRaw);
            await PublishFailedAsync(toRaw, "invalid_phone", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await PublishFailedAsync(to, "missing_text", ct);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // ═══ 1) حل المستأجر من WhatsAppTenants ═══
        var account = await db.WhatsAppTenants.AsNoTracking()
            .FirstOrDefaultAsync(a => a.PhoneNumberId == phoneId && a.IsActive, ct);

        if (account is null)
        {
            _log.LogWarning("whatsapp.send: no active tenant for phoneId={P}", phoneId);
            await PublishFailedAsync(to, "no_active_tenant", ct);
            return;
        }

        // ═══ 2) فحص نافذة 24 ساعة (للنص فقط) ═══
        var conv = await db.WhatsAppConversations
            .FirstOrDefaultAsync(c => c.TenantId == account.TenantId
                                   && c.PhoneNumberId == account.PhoneNumberId
                                   && c.ContactPhone == to, ct);

        var now = DateTime.UtcNow;
        var windowOpen = conv?.LastInboundAtUtc.HasValue == true
                      && (now - conv.LastInboundAtUtc.Value) < TimeSpan.FromHours(24);

        if (!windowOpen)
        {
            _log.LogWarning("24h window closed for {To}", to);
            await PublishFailedAsync(to, "window_closed_use_template", ct);
            return;
        }

        // ═══ 3) إنشاء Conversation إن لم توجد ═══
        if (conv is null)
        {
            conv = new WhatsAppConversationEntity
            {
                TenantId = account.TenantId,
                PhoneNumberId = account.PhoneNumberId,
                ContactPhone = to,
                Status = "open",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.WhatsAppConversations.Add(conv);
            await db.SaveChangesAsync(ct);
        }

        // ═══ 4) بناء payload ═══
        var payloadJson = JsonSerializer.Serialize(new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to,
            type = "text",
            text = new { body = text, preview_url = false }
        });

        // ═══ 5) إنشاء MessageEntity (Outbound, Queued) ═══
        var message = new WhatsAppMessageEntity
        {
            TenantId = account.TenantId,
            ConversationId = conv.Id,
            Direction = "Outbound",
            MessageType = "text",
            ContentJson = payloadJson,
            TextPreview = text.Length > 200 ? text[..200] : text,
            Status = "Queued",
            TimestampUtc = now,
            CreatedAtUtc = now,
            Wamid = null
        };
        db.WhatsAppMessages.Add(message);
        await db.SaveChangesAsync(ct);

        // ═══ 6) إنشاء OutboxEntity مرتبطة بالرسالة ═══
        db.WhatsAppOutbox.Add(new WhatsAppOutboxEntity
        {
            TenantId = account.TenantId,
            CorrelationId = evt.Id.ToString("N"),
            IdempotencyKey = $"manual:{evt.Id}",
            MessageId = message.Id,
            PhoneNumberId = account.PhoneNumberId,
            RecipientPhone = to,
            MessageType = "text",
            PayloadJson = payloadJson,
            Status = "Pending",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
            UpdatedAtUtc = now,
            MaxAttempts = 10
        });

        try
        {
            await db.SaveChangesAsync(ct);
            _log.LogInformation(
                "whatsapp.send {EvtId} → Message {Mid} + Outbox (to={To})",
                evt.Id, message.Id, to);
        }
        catch (DbUpdateException)
        {
            _log.LogInformation("Duplicate whatsapp.send for {EvtId}", evt.Id);
        }
    }

    private async Task PublishFailedAsync(string? to, string error, CancellationToken ct)
    {
        try
        {
            await _bus.PublishAsync(new PlatformEvent
            {
                Type = "whatsapp.failed",
                Source = "whatsapp",
                Payload = JsonSerializer.SerializeToElement(new { to, error })
            }, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to publish whatsapp.failed"); }
    }

    private static string? GetString(PlatformEvent evt, string key)
        => evt.Payload.ValueKind == JsonValueKind.Object
           && evt.Payload.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>تنقية أرقام الهاتف اليمنية.</summary>
    public static string? NormalizePhone(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var p = input.Replace("+", "").Replace(" ", "").Replace("-", "")
                     .Replace("(", "").Replace(")", "").Trim();
        if (p.StartsWith("00")) p = p[2..];
        if (p.StartsWith("0")) p = "967" + p[1..];
        if (!p.StartsWith("967")) p = "967" + p;
        return Regex.IsMatch(p, @"^9677[0-8]\d{7}$") ? p : null;
    }
}
