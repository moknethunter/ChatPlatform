using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>
/// يقرأ الرسائل الواردة من WhatsAppWebhookInbox ويعالجها:
/// 1) يستخرج بيانات الرسالة من PayloadJson
/// 2) يبحث/ينشئ محادثة
/// 3) يحفظ الرسالة الواردة في WhatsAppMessages
/// 4) يحدّث نافذة 24 ساعة (LastInboundAtUtc + WindowExpiresAtUtc)
/// 5) ينشر whatsapp.inbound على IEventBus (للتسجيل والمحوّلات الأخرى)
/// 6) يعلّم الصف كـ Done
/// </summary>
public sealed class WhatsAppInboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly ILogger<WhatsAppInboxWorker> _log;

    public WhatsAppInboxWorker(
        IServiceScopeFactory scopeFactory,
        IEventBus bus,
        ILogger<WhatsAppInboxWorker> log)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("WhatsAppInboxWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                var delay = processed == 0
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromMilliseconds(200);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected error in inbox loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _log.LogInformation("WhatsAppInboxWorker stopped");
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = DateTime.UtcNow;

        var batch = await db.WhatsAppWebhookInbox
            .Where(x => (x.Status == null || x.Status == "Pending")
                     && x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.ReceivedAtUtc)
            .Take(20)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        foreach (var row in batch)
        {
            try
            {
                await ProcessOneAsync(db, row, ct);
                row.Status = "Done";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to process inbox row {Id}", row.Id);
                row.Status = "Pending";
                row.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1);
            }
            await db.SaveChangesAsync(ct);
        }

        return batch.Count;
    }

    private async Task ProcessOneAsync(PlatformDbContext db, WhatsAppWebhookInboxEntity row, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(row.PayloadJson);
        var msg = doc.RootElement;

        var wamid = GetStr(msg, "id");
        var from = GetStr(msg, "from");
        var type = GetStr(msg, "type") ?? "unknown";

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(wamid))
        {
            _log.LogWarning("Inbox {Id}: missing from/wamid, skipping", row.Id);
            return;
        }

        // استخراج النص (إن وُجد)
        string? text = null;
        if (type == "text" && msg.TryGetProperty("text", out var textObj))
            text = GetStr(textObj, "body");

        var preview = text ?? $"[{type}]";
        if (preview.Length > 200) preview = preview[..200];

        var now = DateTime.UtcNow;

        // ═══ 1) ابحث/أنشئ المحادثة ═══
        var conv = await db.WhatsAppConversations
            .FirstOrDefaultAsync(c => c.TenantId == row.TenantId
                                   && c.PhoneNumberId == row.PhoneNumberId
                                   && c.ContactPhone == from, ct);

        if (conv is null)
        {
            conv = new WhatsAppConversationEntity
            {
                TenantId = row.TenantId,
                PhoneNumberId = row.PhoneNumberId,
                ContactPhone = from,
                Status = "open",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastInboundAtUtc = now,
                WindowExpiresAtUtc = now.AddHours(24)
            };
            db.WhatsAppConversations.Add(conv);
            await db.SaveChangesAsync(ct);  // ← لضمان توليد conv.Id (Guid)
        }
        else
        {
            conv.LastInboundAtUtc = now;
            conv.WindowExpiresAtUtc = now.AddHours(24);
            conv.UpdatedAtUtc = now;
            if (conv.Status != "open") conv.Status = "open";
        }

        // ═══ 2) تحقق من التكرار (Wamid فريد) ═══
        var alreadyExists = await db.WhatsAppMessages
            .AnyAsync(m => m.Wamid == wamid, ct);

        Guid messageId;
        if (!alreadyExists)
        {
            var message = new WhatsAppMessageEntity
            {
                TenantId = row.TenantId,
                ConversationId = conv.Id,        // ← Guid
                Direction = "Inbound",
                MessageType = type,
                ContentJson = row.PayloadJson,
                TextPreview = preview,
                Status = "Received",
                TimestampUtc = now,
                CreatedAtUtc = now,
                Wamid = wamid
            };
            db.WhatsAppMessages.Add(message);
            await db.SaveChangesAsync(ct);       // ← لضمان توليد message.Id (Guid)
            messageId = message.Id;
        }
        else
        {
            var existing = await db.WhatsAppMessages.AsNoTracking()
                .Where(m => m.Wamid == wamid)
                .Select(m => new { m.Id })
                .FirstAsync(ct);
            messageId = existing.Id;
        }

        // ═══ 3) نشر الحدث على IEventBus ═══
        // ملاحظة: SignalRAdapter محذوف حالياً. الحدث يُستخدم للتسجيل
        // وللمحوّلات الأخرى (ConsoleAdapter, ChatAdapter...).
        try
        {
            await _bus.PublishAsync(new PlatformEvent
            {
                Type = "whatsapp.inbound",
                Source = "whatsapp",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    conversationId = conv.Id,
                    messageId,
                    from,
                    text,
                    type,
                    wamid,
                    timestampUtc = now
                })
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to publish whatsapp.inbound for {Wamid}", wamid);
        }

        _log.LogInformation(
            "Inbound {Wamid} from {From} → conv {ConvId}, msg {MsgId}",
            wamid, from, conv.Id, messageId);
    }

    private static string? GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
