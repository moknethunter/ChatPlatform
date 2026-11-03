using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppOutboxWorker : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<WhatsAppOutboxWorker> _logger;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public WhatsAppOutboxWorker(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protectionProvider,
        ILogger<WhatsAppOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protectionProvider.CreateProtector("BusinessToken");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WhatsApp outbox worker started: {LeaseOwner}", _leaseOwner);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessOneAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromMilliseconds(1500), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "WhatsApp outbox worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var cloudClient = scope.ServiceProvider.GetRequiredService<IWhatsAppCloudClient>();
        var now = DateTime.UtcNow;

        var candidateId = await db.WhatsAppOutbox.AsNoTracking()
            .Where(x => x.Attempts < x.MaxAttempts &&
                ((x.Status == "Pending" && x.NextAttemptAtUtc <= now) ||
                 (x.Status == "Processing" && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now))))
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidateId is null)
            return false;

        var claimed = await db.WhatsAppOutbox
            .Where(x => x.Id == candidateId.Value && x.Attempts < x.MaxAttempts &&
                ((x.Status == "Pending" && x.NextAttemptAtUtc <= now) ||
                 (x.Status == "Processing" && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now))))
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "Processing")
                .SetProperty(x => x.LeaseOwner, _leaseOwner)
                .SetProperty(x => x.LeaseUntilUtc, now.Add(LeaseDuration))
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);
        if (claimed != 1)
            return true;

        var item = await db.WhatsAppOutbox.AsNoTracking()
            .SingleAsync(x => x.Id == candidateId.Value, cancellationToken);
        var account = await db.WhatsAppTenants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == item.TenantId &&
                x.PhoneNumberId == item.PhoneNumberId && x.IsActive, cancellationToken);
        if (account is null)
        {
            await FinishAsync(db, item, "Failed", "account_not_found", null, cancellationToken);
            return true;
        }

        string accessToken;
        try
        {
            accessToken = _protector.Unprotect(account.BusinessTokenEncrypted);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "WhatsApp token decryption failed for tenant {TenantId}", item.TenantId);
            await FinishAsync(db, item, "Failed", "token_decryption_failed", null, cancellationToken);
            return true;
        }

        WhatsAppSendResult result;
        try
        {
            result = await cloudClient.SendAsync(item.PhoneNumberId, accessToken, item.PayloadJson, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "WhatsApp send result is unknown for outbox {OutboxId}", item.Id);
            await FinishAsync(db, item, "NeedsReview", "unknown_send_result", null, cancellationToken);
            return true;
        }

        switch (result.State)
        {
            case WhatsAppSendState.Accepted:
                await FinishAsync(db, item, "Sent", null, result.MetaMessageId, cancellationToken);
                break;
            case WhatsAppSendState.Retryable:
                await RetryAsync(db, item, result.ErrorCode ?? "temporary_error", result.RetryAfterSeconds, cancellationToken);
                break;
            case WhatsAppSendState.PermanentFailure:
                await FinishAsync(db, item, "Failed", result.ErrorCode, null, cancellationToken);
                break;
            default:
                await FinishAsync(db, item, "NeedsReview", result.ErrorCode ?? "unknown_result", null, cancellationToken);
                break;
        }

        return true;
    }

    private async Task FinishAsync(
        PlatformDbContext db,
        WhatsAppOutboxEntity item,
        string status,
        string? error,
        string? metaMessageId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var affected = await db.WhatsAppOutbox
            .Where(x => x.Id == item.Id && x.Status == "Processing" &&
                x.LeaseOwner == _leaseOwner && x.LeaseUntilUtc > now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.MetaMessageId, metaMessageId)
                .SetProperty(x => x.SentAtUtc, status == "Sent" ? now : (DateTime?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogWarning("Outbox {OutboxId} lease was lost; ignoring stale update", item.Id);
            return;
        }

        if (item.MessageId is Guid messageId)
        {
            var updatedMessages = await db.WhatsAppMessages
                .Where(x => x.Id == messageId && x.TenantId == item.TenantId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, status), cancellationToken);
            if (updatedMessages != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError("Message {MessageId} missing while completing outbox {OutboxId}", messageId, item.Id);
                return;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RetryAsync(
        PlatformDbContext db,
        WhatsAppOutboxEntity item,
        string error,
        int? retryAfterSeconds,
        CancellationToken cancellationToken)
    {
        var delaySeconds = retryAfterSeconds ??
            (int)Math.Min(300, Math.Pow(2, Math.Min(item.Attempts, 8)));
        delaySeconds = Math.Max(1, delaySeconds) + Random.Shared.Next(0, 4);
        var isDeadLetter = item.Attempts >= item.MaxAttempts;
        var status = isDeadLetter ? "DeadLetter" : "Pending";
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var affected = await db.WhatsAppOutbox
            .Where(x => x.Id == item.Id && x.Status == "Processing" &&
                x.LeaseOwner == _leaseOwner && x.LeaseUntilUtc > now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.NextAttemptAtUtc, now.AddSeconds(delaySeconds))
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogWarning("Retry skipped because lease was lost: {OutboxId}", item.Id);
            return;
        }

        if (isDeadLetter && item.MessageId is Guid messageId)
        {
            await db.WhatsAppMessages
                .Where(x => x.Id == messageId && x.TenantId == item.TenantId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "Failed"), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
