using ChatPlatform.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>
/// Sends persisted WhatsApp outbox rows. Ambiguous HTTP outcomes are marked Unknown,
/// never blindly retried, to avoid duplicate customer messages.
/// </summary>
public sealed class WhatsAppOutboxWorker : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtectionProvider _protectionProvider;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppOutboxWorker> _logger;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public WhatsAppOutboxWorker(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protectionProvider,
        IOptions<WhatsAppOptions> options,
        ILogger<WhatsAppOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _protectionProvider = protectionProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WhatsApp Outbox Worker started ({Owner})", _leaseOwner);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessOneAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WhatsApp Outbox Worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var cloud = scope.ServiceProvider.GetRequiredService<IWhatsAppCloudClient>();
        var now = DateTime.UtcNow;

        // A row left in Processing after a crash may already have reached Meta.
        // Mark it Unknown rather than sending it again and risking a duplicate.
        await db.WhatsAppOutbox
            .Where(x => x.Status == "Processing" && x.LeaseUntilUtc != null && x.LeaseUntilUtc <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "Unknown")
                .SetProperty(x => x.LastError, "lease_expired_remote_result_unknown")
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), ct);

        var candidateId = await db.WhatsAppOutbox
            .AsNoTracking()
            .Where(x => x.Attempts < x.MaxAttempts &&
                        (x.Status == "Pending" || x.Status == "Retry") &&
                        x.NextAttemptAtUtc <= now &&
                        (x.ScheduledForUtc == null || x.ScheduledForUtc <= now))
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);

        if (candidateId is null)
            return false;

        var claimed = await db.WhatsAppOutbox
            .Where(x => x.Id == candidateId.Value && x.Attempts < x.MaxAttempts &&
                        (x.Status == "Pending" || x.Status == "Retry") &&
                        x.NextAttemptAtUtc <= now &&
                        (x.ScheduledForUtc == null || x.ScheduledForUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "Processing")
                .SetProperty(x => x.LeaseOwner, _leaseOwner)
                .SetProperty(x => x.LeaseUntilUtc, now.Add(LeaseDuration))
                .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                .SetProperty(x => x.UpdatedAtUtc, now), ct);

        if (claimed == 0)
            return true;

        var item = await db.WhatsAppOutbox
            .AsNoTracking()
            .SingleAsync(x => x.Id == candidateId.Value, ct);

        var tenant = await db.WhatsAppTenants
            .AsNoTracking()
            .Where(x => x.TenantId == item.TenantId &&
                        x.PhoneNumberId == item.PhoneNumberId && x.IsActive)
            .Select(x => new { x.BusinessTokenEncrypted })
            .SingleOrDefaultAsync(ct);

        if (tenant is null || string.IsNullOrWhiteSpace(tenant.BusinessTokenEncrypted))
        {
            await CompleteAsync(db, item.Id, "Failed", null, "active_tenant_or_token_not_found", null, ct);
            return true;
        }

        string accessToken;
        try
        {
            accessToken = _protectionProvider
                .CreateProtector("BusinessToken")
                .Unprotect(tenant.BusinessTokenEncrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not decrypt WhatsApp token for tenant {TenantId}", item.TenantId);
            await CompleteAsync(db, item.Id, "Failed", null, "token_decryption_failed", null, ct);
            return true;
        }

        var result = await cloud.SendAsync(
            _options.GraphApiVersion,
            item.PhoneNumberId,
            accessToken,
            item.PayloadJson,
            ct);

        if (result.IsSuccess)
        {
            await CompleteAsync(db, item.Id, "Sent", result.MetaMessageId, null, null, ct);
            _logger.LogInformation("Outbox {OutboxId} accepted by Meta; message id {MetaMessageId}", item.Id, result.MetaMessageId);
            return true;
        }

        if (result.IsAmbiguous)
        {
            await CompleteAsync(db, item.Id, "Unknown", null, result.ErrorCode, null, ct);
            _logger.LogWarning("Outbox {OutboxId} has an unknown Meta result; manual reconciliation is required", item.Id);
            return true;
        }

        if (result.IsRetryable && item.Attempts < item.MaxAttempts)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, Math.Min(item.Attempts, 10))));
            await CompleteAsync(db, item.Id, "Retry", null, result.ErrorCode, DateTime.UtcNow.Add(delay), ct);
            return true;
        }

        await CompleteAsync(db, item.Id, "Failed", null, result.ErrorCode, null, ct);
        return true;
    }

    private async Task CompleteAsync(
        PlatformDbContext db,
        Guid id,
        string status,
        string? metaMessageId,
        string? error,
        DateTime? nextAttempt,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var updated = await db.WhatsAppOutbox
            .Where(x => x.Id == id && x.Status == "Processing" && x.LeaseOwner == _leaseOwner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.MetaMessageId, metaMessageId)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.SentAtUtc, status == "Sent" ? now : (DateTime?)null)
                .SetProperty(x => x.NextAttemptAtUtc, nextAttempt ?? now)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), ct);

        if (updated == 0)
            _logger.LogWarning("Outbox lease lost before completing item {OutboxId}", id);
    }
}
