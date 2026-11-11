using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

/// <summary>
/// ⚠️ للتجربة فقط — احذفه بعد أول اختبار ناجح.
/// يزرع 3 مؤسسات ثابتة + يحفظ التوكن المشفر في WhatsAppTenants.
/// </summary>
[ApiController, Route("dev/seed")]
public sealed class DevSeedController : ControllerBase
{
    // ===== الـ TenantId الثابتة - لا تغيرها أبداً =====
    public const string Tenant1Id = "tenant-mokco-001";     // مؤسستك mokco
    public const string Tenant2Id = "tenant-mohammed-002";   // مؤسسة صديقك محمد
    public const string Tenant3Id = "tenant-empty-003";      // الفارغة المحجوزة

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

    // ===== 1. زرع المؤسسات الثلاث - ناديه أول مرة بس =====
    // POST /dev/seed/init-tenants
    [HttpPost("init-tenants")]
    public async Task<IActionResult> InitTenants(CancellationToken ct)
    {
        var tenants = new[]
        {
            new TenantEntity { Id = Tenant1Id, Name = "mokco", Phone = "967774821733", IsActive = true },
            new TenantEntity { Id = Tenant2Id, Name = "محمد", Phone = "967777616175", IsActive = true },
            new TenantEntity { Id = Tenant3Id, Name = "مؤسسة فارغة - للحجز", Phone = null, IsActive = false },
        };

        foreach (var t in tenants)
        {
            var exists = await _db.Tenants.AnyAsync(x => x.Id == t.Id, ct);
            if (!exists)
                _db.Tenants.Add(t);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { success = true, tenants = new[] { Tenant1Id, Tenant2Id, Tenant3Id } });
    }

    // ===== 2. ربط الواتساب =====
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

        var encrypted = _protector.Protect(req.BusinessToken);

        var existing = await _db.WhatsAppTenants
            .FirstOrDefaultAsync(x => x.PhoneNumberId == req.PhoneNumberId, ct);

        if (existing != null)
        {
            existing.WabaId = req.WabaId;
            existing.BusinessTokenEncrypted = encrypted;
            existing.IsActive = true;
        }
        else
        {
            _db.WhatsAppTenants.Add(new WhatsAppTenantEntity
            {
                TenantId = req.TenantId ?? Tenant1Id,
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
            tenantId = req.TenantId ?? Tenant1Id,
            phoneNumberId = req.PhoneNumberId
        });
    }

    [HttpGet("list")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenants = await _db.WhatsAppTenants
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
