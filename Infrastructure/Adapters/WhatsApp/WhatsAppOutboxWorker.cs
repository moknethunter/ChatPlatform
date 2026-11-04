using ChatPlatform.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppOutboxWorker : BackgroundService
{
    private const int MaxAttempts = 10;
    private const int LeaseSeconds = 60;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<WhatsAppOutboxWorker> _logger;
    private readonly string _leaseOwner;

    public WhatsAppOutboxWorker(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protection,
        ILogger<WhatsAppOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protection.CreateProtector("BusinessToken");
        _logger = logger;
        _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WhatsAppOutboxWorker started (owner={Owner})", _leaseOwner);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOneAsync(stoppingToken);
                if (!processed) await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox worker loop error");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<IWhatsAppCloudClient>();
        var now = DateTime.UtcNow;

        // 1) اقرأ المرشح AsNoTracking
        var candidateId = await db.WhatsAppOutbox.AsNoTracking()
            .Where(x => ((x.Status == "Pending" && x.NextAttemptAtUtc <= now)
                      || (x.Status == "Processing" && x.LeaseUntilUtc < now))
                     && x.Attempts < x.MaxAttempts)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);

        if (candidateId == null) return false;

        // 2) حجز ذري
        var claimed = await db.WhatsAppOutbox
            .Where(x => x.Id == candidateId.Value
                     && ((x.Status == "Pending" && x.NextAttemptAtUtc <= now)
                      || (x.Status == "Processing" && x.LeaseUntilUtc < now)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "Processing")
                .SetProperty(x => x.LeaseUntilUtc, now.AddSeconds(LeaseSeconds))
                .SetProperty(x => x.LeaseOwner, _leaseOwner)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);

        if (claimed == 0) return true;

        db.ChangeTracker.Clear();

        var item = await db.WhatsAppOutbox.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == candidateId.Value, ct);
        if (item == null) return true;

        // 3) جلب التوكن
        var account = await db.WhatsAppTenants.AsNoTracking()
            .FirstOrDefaultAsync(a => a.PhoneNumberId == item.PhoneNumberId && a.IsActive, ct);

        if (account == null)
        {
            await FailPermanentAsync(db, item.Id, "no_active_tenant", ct);
            return true;
        }

        string token;
        try { token = _protector.Unprotect(account.BusinessTokenEncrypted); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token decrypt failed for phoneId={P}", item.PhoneNumberId);
            await FailPermanentAsync(db, item.Id, "token_decrypt_failed", ct);
            return true;
        }

        // 4) الإرسال
        var result = await client.SendRawAsync(item.PhoneNumberId, token, item.PayloadJson, ct);

        if (result.IsSuccess)
            await MarkSentAsync(db, item.Id, item.MessageId, result.MetaMessageId, ct);
        else if (result.IsTransient)
            await RetryAsync(db, item.Id, result.Raw ?? "transient", ct);
        else
            await FailPermanentAsync(db, item.Id, result.Raw ?? "permanent", ct);

        return true;
    }

    private async Task MarkSentAsync(
        PlatformDbContext db, Guid outboxId, Guid? messageId,
        string? metaId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var affected = await db.WhatsAppOutbox
            .Where(x => x.Id == outboxId && x.Status == "Processing" && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "Sent")
                .SetProperty(x => x.SentAtUtc, now)
                .SetProperty(x => x.MetaMessageId, metaId)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LastError, (string?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), ct);

        if (affected == 0)
        {
            _logger.LogWarning("Outbox {Id} lost lease before MarkSent", outboxId);
            return;
        }

        // تحديث MessageEntity المرتبطة
        if (messageId.HasValue)
        {
            await db.WhatsAppMessages
                .Where(m => m.Id == messageId.Value)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, "Sent")
                    .SetProperty(m => m.Wamid, metaId), ct);
        }

        _logger.LogInformation("Outbox {Id} sent (MetaId={MetaId})", outboxId, metaId);
    }

    private async Task RetryAsync(
        PlatformDbContext db, Guid outboxId, string error, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var attempts = await db.WhatsAppOutbox.AsNoTracking()
            .Where(x => x.Id == outboxId).Select(x => x.Attempts).FirstOrDefaultAsync(ct);
        var isDead = attempts >= MaxAttempts;
        var delay = Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)));

        await db.WhatsAppOutbox
            .Where(x => x.Id == outboxId && x.Status == "Processing" && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, isDead ? "DeadLetter" : "Pending")
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.NextAttemptAtUtc, now.AddSeconds(delay))
                .SetProperty(x => x.UpdatedAtUtc, now), ct);

        _logger.LogWarning("Outbox {Id} retry in {Delay}s: {Error}", outboxId, delay, error);
    }

    private async Task FailPermanentAsync(
        PlatformDbContext db, Guid outboxId, string error, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var messageId = await db.WhatsAppOutbox.AsNoTracking()
            .Where(x => x.Id == outboxId).Select(x => x.MessageId).FirstOrDefaultAsync(ct);

        await db.WhatsAppOutbox
            .Where(x => x.Id == outboxId && x.Status == "Processing" && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "Failed")
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.UpdatedAtUtc, now), ct);

        if (messageId.HasValue)
        {
            await db.WhatsAppMessages
                .Where(m => m.Id == messageId.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "Failed"), ct);
        }

        _logger.LogError("Outbox {Id} permanently failed: {Error}", outboxId, error);
    }
}
