using ChatPlatform.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/whatsapp/tenants")]
public class TenantsController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public TenantsController(PlatformDbContext db) => _db = db;

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
                displayName = t.PhoneNumberId,  // ← سنستخدم phoneNumberId كاسم افتراضي
                isActive = t.IsActive,
            })
            .ToListAsync(ct);

        return Ok(tenants);
    }

    public record CreateTenantRequest(string TenantId, string PhoneNumberId, string AccessToken);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TenantId) || string.IsNullOrWhiteSpace(req.PhoneNumberId))
            return BadRequest(new { error = "TenantId and PhoneNumberId are required" });

        var existing = await _db.WhatsAppTenants
            .FirstOrDefaultAsync(t => t.PhoneNumberId == req.PhoneNumberId, ct);

        if (existing != null)
        {
            existing.TenantId = req.TenantId;
            existing.AccessToken = req.AccessToken;
            existing.IsActive = true;
        }
        else
        {
            _db.WhatsAppTenants.Add(new WhatsAppTenantEntity
            {
                TenantId = req.TenantId,
                PhoneNumberId = req.PhoneNumberId,
                AccessToken = req.AccessToken,
                IsActive = true
            });
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "saved", phoneNumberId = req.PhoneNumberId });
    }
}
