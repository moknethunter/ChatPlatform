using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppOutboxWorker : BackgroundService
{
    private const int LeaseSeconds = 60;
    private const int MaxErrorDelaySeconds = 300;

    private const string StatusPending = "Pending";
    private const string StatusProcessing = "Processing";
    private const string StatusSent = "Sent";
    private const string StatusFailed = "Failed";
    private const string StatusDeadLetter = "DeadLetter";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WhatsAppTokenProtection _protector;
    private readonly ILogger<WhatsAppOutboxWorker> _logger;

    private readonly string _leaseOwner = 
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public WhatsAppOutboxWorker(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protection,
        ILogger<WhatsAppOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = new WhatsAppTokenProtection(protection);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WhatsAppOutboxWorker started. Owner={Owner}", _leaseOwner);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOneAsync(stoppingToken);
                if (!processed) await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox worker loop error.");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("WhatsAppOutboxWorker stopped.");
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<IWhatsAppCloudClient>();
        var now = DateTime.UtcNow;

        // 1. البحث عن رسالة جاهزة للإرسال
        var candidate = await db.WhatsAppOutbox
            .AsNoTracking()
            .Where(x => ((x.Status == StatusPending && x.NextAttemptAtUtc <= now) ||
                         (x.Status == StatusProcessing && x.LeaseUntilUtc < now))
                        && (x.ScheduledForUtc == null || x.ScheduledForUtc <= now)
                        && (x.Attempts < x.MaxAttempts || (x.Status == StatusProcessing && x.IdempotencyKey.StartsWith("automation:"))))
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new { x.Id, x.Status })
            .FirstOrDefaultAsync(ct);

        if (candidate == null) return false;

        // 2. الحجز الذري للرسالة
        var claimed = await db.WhatsAppOutbox
            .Where(x => x.Id == candidate.Id && x.Status == candidate.Status
                        && ((x.Status == StatusPending && x.NextAttemptAtUtc <= now) ||
                            (x.Status == StatusProcessing && x.LeaseUntilUtc < now))
                        && (x.Attempts < x.MaxAttempts || (x.Status == StatusProcessing && x.IdempotencyKey.StartsWith("automation:"))))
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, StatusProcessing)
                .SetProperty(x => x.LeaseUntilUtc, now.AddSeconds(LeaseSeconds))
                .SetProperty(x => x.LeaseOwner, _leaseOwner)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);

        if (claimed == 0) return true;

        db.ChangeTracker.Clear();

        var item = await db.WhatsAppOutbox.AsNoTracking().FirstOrDefaultAsync(x => x.Id == candidate.Id, ct);
        if (item == null) return true;
        var automated = item.IdempotencyKey.StartsWith("automation:", StringComparison.Ordinal);
        // A previous worker might have sent successfully and died before saving the result.
        // Do not resend an automated reply after an expired processing lease.
        if (automated && candidate.Status == StatusProcessing)
        {
            await MarkUncertainAsync(db, item, "automation_expired_lease", ct);
            return true;
        }

        // 3. جلب الحساب والتحقق من المستأجر
        var account = await db.WhatsAppTenants
            .AsNoTracking()
            .Include(x => x.Tenant)
            .FirstOrDefaultAsync(x => x.PhoneNumberId == item.PhoneNumberId && x.IsActive, ct);

        if (account == null || account.Tenant == null || !account.Tenant.IsActive || account.TenantId != item.TenantId)
        {
            await FailPermanentAsync(db, item, "no_active_matching_tenant", ct);
            return true;
        }

        // Recheck the customer-service window immediately before dispatching an automated reply.
        if (automated)
        {
            var at = await db.WhatsAppConversations.AsNoTracking().Where(c => c.TenantId == item.TenantId &&
                c.PhoneNumberId == item.PhoneNumberId && c.ContactPhone == item.RecipientPhone)
                .Select(c => c.LastInboundAtUtc).FirstOrDefaultAsync(ct);
            if (at == null || at > now || now - at.Value >= TimeSpan.FromHours(24))
            {
                await FailPermanentAsync(db, item, "automation_window_closed", ct);
                return true;
            }
        }

        // 4. فك تشفير رمز الوصول
        string token;
        try
        {
            if (string.IsNullOrWhiteSpace(account.BusinessTokenEncrypted))
                throw new InvalidOperationException("Encrypted token is empty.");
            token = _protector.Unprotect(account.BusinessTokenEncrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token decrypt failed. PhoneNumberId={PhoneId}", item.PhoneNumberId);
            await FailPermanentAsync(db, item, "token_decrypt_failed", ct);
            return true;
        }

        // A takeover cancels pending automation and blocks claimed-but-not-yet-sent replies.
        // A request already in flight cannot be recalled.
        if(automated && await db.ConversationAutomation.AsNoTracking().AnyAsync(s=>s.TenantId==item.TenantId && s.Mode=="human" &&
            db.WhatsAppMessages.Any(m=>m.Id==item.MessageId && m.TenantId==item.TenantId && m.ConversationId==s.ConversationId),ct))
        {
            await FailPermanentAsync(db,item,"human_handoff",ct);return true;
        }

        // 5. إرسال الرسالة عبر WhatsApp Cloud API
        var result = await client.SendRawAsync(item.PhoneNumberId, token, item.PayloadJson, ct);

        // 6. معالجة نتيجة الإرسال
        if (automated && ((result.IsSuccess && string.IsNullOrWhiteSpace(result.MetaMessageId))
            || (result.IsTransient && !(result.Raw?.StartsWith("http_429:", StringComparison.Ordinal) ?? false))))
        {
            // HTTP 5xx, network/timeout and malformed successful responses can be ambiguous;
            // only an explicit 429 rejection is automatically retried for automation.
            await MarkUncertainAsync(db, item, "automation_delivery_uncertain", ct);
        }
        else if (result.IsSuccess)
        {
            await MarkSentAsync(db, item, result.MetaMessageId, ct);
        }
        else if (result.IsTransient)
        {
            await RetryAsync(db, item, result.Raw ?? "transient_error", ct);
        }
        else
        {
            await FailPermanentAsync(db, item, result.Raw ?? "permanent_error", ct);
        }

        return true;
    }

    private async Task MarkUncertainAsync(PlatformDbContext db, WhatsAppOutboxEntity item, string reason, CancellationToken ct)
    {
        const string unknown = "DeliveryUnknown";
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var affected = await db.WhatsAppOutbox
            .Where(x => x.Id == item.Id && x.TenantId == item.TenantId && x.Status == StatusProcessing && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, unknown).SetProperty(x => x.LastError, reason)
                .SetProperty(x => x.UpdatedAtUtc, now).SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null), ct);
        if (affected == 1 && item.MessageId.HasValue)
            await db.WhatsAppMessages.Where(x => x.Id == item.MessageId.Value && x.TenantId == item.TenantId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, unknown), ct);
        await tx.CommitAsync(ct);
        _logger.LogWarning("Automated outbox {Id} marked delivery unknown; not automatically retried.", item.Id);
    }

    private async Task MarkSentAsync(PlatformDbContext db, WhatsAppOutboxEntity item, string? metaId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // التحديث المباشر الآمن لـ EF Core
        var affected = await db.WhatsAppOutbox
            .Where(x => x.Id == item.Id && x.Status == StatusProcessing && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, StatusSent)
                .SetProperty(x => x.SentAtUtc, now)
                .SetProperty(x => x.MetaMessageId, metaId)
                .SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null), ct);

        if (affected == 0)
        {
            _logger.LogWarning("Outbox {Id} lost its lease before completion.", item.Id);
            return;
        }

        if (item.MessageId.HasValue)
        {
            await db.WhatsAppMessages
                .Where(m => m.Id == item.MessageId.Value)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(m => m.Status, StatusSent)
                    .SetProperty(m => m.Wamid, metaId), ct);
        }

        _logger.LogInformation("Outbox {Id} sent. MetaMessageId={MetaId}", item.Id, metaId);
    }

    private async Task RetryAsync(PlatformDbContext db, WhatsAppOutboxEntity item, string error, CancellationToken ct)
    {
        var dead = item.Attempts >= Math.Max(1, item.MaxAttempts);
        var delaySeconds = Math.Min(MaxErrorDelaySeconds, Math.Pow(2, Math.Min(item.Attempts, 8)));
        var now = DateTime.UtcNow;

        // التحديث المباشر الآمن لـ EF Core
        await db.WhatsAppOutbox
            .Where(x => x.Id == item.Id && x.Status == StatusProcessing && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, dead ? StatusDeadLetter : StatusPending)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.NextAttemptAtUtc, now.AddSeconds(delaySeconds))
                .SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null), ct);

        _logger.LogWarning("Outbox {Id} retry. Attempts={Attempts}, Dead={Dead}", item.Id, item.Attempts, dead);
    }

    private async Task FailPermanentAsync(PlatformDbContext db, WhatsAppOutboxEntity item, string error, CancellationToken ct)
    {
        // التحديث المباشر الآمن لـ EF Core
        var affected = await db.WhatsAppOutbox
            .Where(x => x.Id == item.Id && x.Status == StatusProcessing && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, StatusFailed)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null), ct);

        if (affected == 0) return;

        if (item.MessageId.HasValue)
        {
            await db.WhatsAppMessages
                .Where(m => m.Id == item.MessageId.Value && m.TenantId == item.TenantId)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, StatusFailed), ct);
        }

        _logger.LogError("Outbox {Id} permanently failed: {Error}", item.Id, error);
    }
}
