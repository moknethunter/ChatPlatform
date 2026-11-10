using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

/// <summary>
/// ⚠️ للتجربة فقط — احذفه بعد أول اختبار ناجح.
/// يحفظ التوكن المشفر في WhatsAppTenants.
/// </summary>
[ApiController, Route("dev/seed")]
public sealed class DevSeedController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<DevSeedController> _log;

    public DevSeedController(
        PlatformDbContext db,
        IDataProtectionProvider protection,
        ILogger<DevSeedController> log)
    {
        _db = db;
        _protector = protection.CreateProtector("BusinessToken");
        _log = log;
    }

    [HttpPost]
    public async Task<IActionResult> Seed([FromBody] SeedReq req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.PhoneNumberId) ||
            string.IsNullOrWhiteSpace(req.WabaId) ||
            string.IsNullOrWhiteSpace(req.BusinessToken))
        {
            return BadRequest(new { error = "missing_fields",
                message = "phoneNumberId, wabaId, businessToken are required" });
        }

        var tenantId = string.IsNullOrWhiteSpace(req.TenantId)
            ? TenantEntity.DefaultTenantId
            : req.TenantId.Trim();

        var tenantExists = await _db.Tenants.AnyAsync(x => x.Id == tenantId, ct);
        if (!tenantExists)
            return BadRequest(new { error = "tenant_not_found", tenantId });

        var encrypted = _protector.Protect(req.BusinessToken);

        var existing = await _db.WhatsAppTenants
            .FirstOrDefaultAsync(x => x.PhoneNumberId == req.PhoneNumberId, ct);

        if (existing != null)
        {
            existing.TenantId = tenantId;
            existing.WabaId = req.WabaId;
            existing.BusinessTokenEncrypted = encrypted;
            existing.IsActive = true;
        }
        else
        {
            _db.WhatsAppTenants.Add(new WhatsAppTenantEntity
            {
                TenantId = tenantId,
                PhoneNumberId = req.PhoneNumberId,
                WabaId = req.WabaId,
                BusinessTokenEncrypted = encrypted,
                IsActive = true
            });
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Seeded tenant: phoneId={P}, wabaId={W}",
            req.PhoneNumberId, req.WabaId);

        return Ok(new
        {
            success = true,
            tenantId,
            phoneNumberId = req.PhoneNumberId
        });
    }

    [HttpGet("list")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenants = await _db.WhatsAppTenants
            .AsNoTracking()
            .Select(t => new
            {
                t.TenantId,
                t.PhoneNumberId,
                t.WabaId,
                t.IsActive,
                HasToken = !string.IsNullOrEmpty(t.BusinessTokenEncrypted)
            })
            .ToListAsync(ct);
        return Ok(tenants);
    }

    public record SeedReq(
        string PhoneNumberId,
        string WabaId,
        string BusinessToken,
        string? TenantId);
}
