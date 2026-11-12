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

public sealed class WhatsAppInboxWorker : BackgroundService
{
    private const int BatchSize = 20;
    private const int LeaseSeconds = 120;
    private const int MaxErrorDelaySeconds = 300;
    private const string StatusPending = "Pending";
    private const string StatusProcessing = "Processing";
    private const string StatusDone = "Done";
    private const string StatusDeadLetter = "DeadLetter";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly ILogger<WhatsAppInboxWorker> _log;

    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

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
        _log.LogInformation("WhatsAppInboxWorker started. Owner={Owner}", _leaseOwner);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                // ذكاء في التوقيت: إذا لم توجد رسائل، انتظر ثانيتين. إذا كانت هناك رسائل، تابع فوراً.
                await Task.Delay(processed == 0 ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(200), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Inbox worker loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _log.LogInformation("WhatsAppInboxWorker stopped.");
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var processed = 0;

        for (var i = 0; i < BatchSize; i++)
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;

            // 1. البحث عن رسالة جاهزة للمعالجة
            var candidateId = await db.WhatsAppWebhookInbox
                .AsNoTracking()
                .Where(x => (x.Status == StatusPending || x.Status == null || (x.Status == StatusProcessing && x.LeaseUntilUtc < now))
                            && x.NextAttemptAtUtc <= now
                            && x.Attempts < x.MaxAttempts)
                .OrderBy(x => x.ReceivedAtUtc)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(ct);

            if (candidateId == null) break;

            // 2. القفل الذري (Atomic Claim) - نضمن أن رسالة واحدة لا يلتقطها إلا عامل واحد
            var claimed = await db.WhatsAppWebhookInbox
                .Where(x => x.Id == candidateId.Value 
                            && (x.Status == StatusPending || x.Status == null || (x.Status == StatusProcessing && x.LeaseUntilUtc < now))
                            && x.Attempts < x.MaxAttempts)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, StatusProcessing)
                    .SetProperty(x => x.LeaseOwner, _leaseOwner)
                    .SetProperty(x => x.LeaseUntilUtc, now.AddSeconds(LeaseSeconds))
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);

            if (claimed == 0) continue;

            db.ChangeTracker.Clear(); // تنظيف الذاكرة

            var row = await db.WhatsAppWebhookInbox.AsNoTracking().FirstOrDefaultAsync(x => x.Id == candidateId.Value, ct);
            if (row == null) continue;

            try
            {
                // 3. المعالجة الفعلية
                await ProcessOneAsync(db, row, ct);
                
                // 4. تحديث الحالة إلى مكتمل
                await MarkCompletedAsync(db, row.Id, ct);
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to process inbox row {Id}", row.Id);
                // 5. تحديث الحالة إلى فشل أو DeadLetter
                await MarkFailedAsync(db, row, ex.Message, ct);
            }
        }

        return processed;
    }

    private async Task ProcessOneAsync(PlatformDbContext db, WhatsAppWebhookInboxEntity row, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(row.PayloadJson);
        var msg = doc.RootElement;

        var wamid = msg.GetString("id");
        var from = msg.GetString("from");
        var type = msg.GetString("type") ?? "unknown";

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(wamid))
        {
            _log.LogWarning("Inbox {Id} has missing sender or message ID.", row.Id);
            return; // سيتم اعتباره مكتملاً لأننا لا نستطيع معالجته
        }

        // منع التكرار (Idempotency)
        if (await db.WhatsAppMessages.AsNoTracking().AnyAsync(x => x.Wamid == wamid, ct))
        {
            _log.LogInformation("Message {Wamid} already exists.", wamid);
            return;
        }

        var text = (type == "text" && msg.TryGetProperty("text", out var textObj)) 
            ? textObj.GetString("body") 
            : null;

        var preview = text ?? $"[{type}]";
        if (preview.Length > 200) preview = preview[..200];

        var now = DateTime.UtcNow;

        // جلب أو إنشاء المحادثة
        var conversation = await db.WhatsAppConversations.FirstOrDefaultAsync(
            x => x.TenantId == row.TenantId && x.PhoneNumberId == row.PhoneNumberId && x.ContactPhone == from, ct);

        if (conversation == null)
        {
            conversation = new WhatsAppConversationEntity
            {
                TenantId = row.TenantId,
                PhoneNumberId = row.PhoneNumberId,
                ContactPhone = from,
                Status = "open",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastInboundAtUtc = now,
                WindowExpiresAtUtc = now.AddHours(24),
                UnreadCount = 1
            };
            db.WhatsAppConversations.Add(conversation);
        }
        else
        {
            conversation.LastInboundAtUtc = now;
            conversation.WindowExpiresAtUtc = now.AddHours(24);
            conversation.UpdatedAtUtc = now;
            conversation.UnreadCount++;
            if (conversation.Status != "open") conversation.Status = "open";
        }

        // إنشاء الرسالة
        var message = new WhatsAppMessageEntity
        {
            TenantId = row.TenantId,
            ConversationId = conversation.Id,
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

        // 🚀 تحسين ذكي: حفظ المحادثة والرسالة معاً في خطوة واحدة (Transaction ضمني)
        await db.SaveChangesAsync(ct);

        // نشر الحدث (Event)
        try
        {
            await _bus.PublishAsync(new PlatformEvent
            {
                Type = "whatsapp.inbound",
                Source = "whatsapp",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    conversationId = conversation.Id,
                    messageId = message.Id,
                    tenantId = row.TenantId,
                    phoneNumberId = row.PhoneNumberId,
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
            // الرسالة محفوظة بأمان في الداتا بيز، لا نريد إفشال العملية بسبب الباص.
            _log.LogError(ex, "Failed to publish inbound event for {Wamid}", wamid);
        }

        _log.LogInformation("Inbound message saved. Wamid={Wamid}, Conversation={ConversationId}", wamid, conversation.Id);
    }

    // ══════════════════════════════════════════════════════════════
    // دوال مساعدة لتحديث الحالة (DRY Principle)
    // ══════════════════════════════════════════════════════════════
    
    private async Task MarkCompletedAsync(PlatformDbContext db, Guid id, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.WhatsAppWebhookInbox
            .Where(x => x.Id == id && x.Status == StatusProcessing && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, StatusDone)
                .SetProperty(x => x.ProcessedAtUtc, now)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, (string?)null), ct);
    }

    private async Task MarkFailedAsync(PlatformDbContext db, WhatsAppWebhookInboxEntity row, string error, CancellationToken ct)
    {
        // ذكاء: قراءة المحاولات من الصف الذي تم جلبه، بدون زيادة خاطئة
        var dead = row.Attempts >= Math.Max(1, row.MaxAttempts);
        var delaySeconds = Math.Min(MaxErrorDelaySeconds, Math.Pow(2, Math.Min(row.Attempts, 8)));
        var nextAttempt = DateTime.UtcNow.AddSeconds(delaySeconds);

        await db.WhatsAppWebhookInbox
            .Where(x => x.Id == row.Id && x.Status == StatusProcessing && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, dead ? StatusDeadLetter : StatusPending)
                .SetProperty(x => x.NextAttemptAtUtc, nextAttempt)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct);
    }
}
