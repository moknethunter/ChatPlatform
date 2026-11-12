using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp.Binding;

public interface IWhatsAppTenantResolver
{
    Task<WhatsAppTenantEntity?> ResolveByPhoneNumberIdAsync(
        string phoneNumberId,
        CancellationToken cancellationToken = default);
}

public sealed class WhatsAppTenantResolver : IWhatsAppTenantResolver
{
    private readonly PlatformDbContext _dbContext;
    private readonly ILogger<WhatsAppTenantResolver> _logger;

    public WhatsAppTenantResolver(
        PlatformDbContext dbContext,
        ILogger<WhatsAppTenantResolver> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<WhatsAppTenantEntity?> ResolveByPhoneNumberIdAsync(
        string phoneNumberId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId))
            return null;

        phoneNumberId = phoneNumberId.Trim();

        var account = await _dbContext.WhatsAppTenants
            .AsNoTracking()
            .Include(x => x.Tenant)
            .SingleOrDefaultAsync(
                x => x.PhoneNumberId == phoneNumberId,
                cancellationToken);

        if (account == null)
        {
            _logger.LogWarning(
                "WhatsApp account not found. PhoneNumberId={PhoneNumberId}",
                phoneNumberId);

            return null;
        }

        if (!account.IsActive)
        {
            _logger.LogWarning(
                "WhatsApp account is inactive. PhoneNumberId={PhoneNumberId}",
                phoneNumberId);

            return null;
        }

        if (account.Tenant == null || !account.Tenant.IsActive)
        {
            _logger.LogWarning(
                "WhatsApp account has no active tenant. PhoneNumberId={PhoneNumberId}",
                phoneNumberId);

            return null;
        }

        if (string.IsNullOrWhiteSpace(account.TenantId))
        {
            _logger.LogWarning(
                "WhatsApp account has an empty TenantId. PhoneNumberId={PhoneNumberId}",
                phoneNumberId);

            return null;
        }

        return account;
    }
}
