using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/whatsapp/tenants")]
public class TenantsController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IDataProtector _protector;

    public TenantsController(
        PlatformDbContext db,
        IDataProtectionProvider protectionProvider)
    {
        _db = db;
        _protector = protectionProvider.CreateProtector("BusinessToken");
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenants = await _db.WhatsAppTenants
            .AsNoTracking()
            .OrderBy(t => t.PhoneNumberId)
            .Select(t => new
            {
                tenantId = t.TenantId,
                phoneNumberId = t.PhoneNumberId,
                displayName = t.PhoneNumberId,
                isActive = t.IsActive,
                hasToken = !string.IsNullOrEmpty(t.BusinessTokenEncrypted)
            })
            .ToListAsync(ct);

        return Ok(tenants);
    }

    public record CreateTenantRequest(
        string TenantId,
        string PhoneNumberId,
        string AccessToken);

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateTenantRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TenantId) ||
            string.IsNullOrWhiteSpace(req.PhoneNumberId) ||
            string.IsNullOrWhiteSpace(req.AccessToken))
        {
            return BadRequest(new
            {
                error = "TenantId, PhoneNumberId and AccessToken are required"
            });
        }

        // تحقق من وجود المستأجر لتجنب خطأ المفتاح الخارجي عند الحفظ.
        var tenantExists = await _db.Tenants
            .AnyAsync(t => t.Id == req.TenantId, ct);

        if (!tenantExists)
        {
            return BadRequest(new { error = "TenantId does not exist" });
        }

        var encryptedToken = _protector.Protect(req.AccessToken);

        var existing = await _db.WhatsAppTenants
            .FirstOrDefaultAsync(
                t => t.PhoneNumberId == req.PhoneNumberId,
                ct);

        if (existing != null)
        {
            existing.TenantId = req.TenantId;
            existing.BusinessTokenEncrypted = encryptedToken;
            existing.IsActive = true;
        }
        else
        {
            _db.WhatsAppTenants.Add(new WhatsAppTenantEntity
            {
                TenantId = req.TenantId,
                PhoneNumberId = req.PhoneNumberId,
                BusinessTokenEncrypted = encryptedToken,
                IsActive = true
            });
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            message = "saved",
            phoneNumberId = req.PhoneNumberId,
            tenantId = req.TenantId
        });
    }
}
