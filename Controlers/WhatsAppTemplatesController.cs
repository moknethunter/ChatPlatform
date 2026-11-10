using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/templates")]
public sealed class WhatsAppTemplatesController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public WhatsAppTemplatesController(PlatformDbContext db)
    {
        _db = db;
    }

    public sealed record CreateTemplateRequest(
        string TenantId,
        string WhatsAppTenantId,
        string Name,
        string Category,
        string Language,
        string Body
    );

    // GET /api/templates?tenantId=...&whatsAppTenantId=...
    [HttpGet]
    public async Task<IActionResult> GetTemplates(
        [FromQuery] string tenantId,
        [FromQuery] string? whatsAppTenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "tenantId is required" });

        var query = _db.WhatsAppTemplates
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(whatsAppTenantId))
        {
            query = query.Where(
                x => x.WhatsAppTenantId == whatsAppTenantId);
        }

        var templates = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Ok(templates);
    }

    // POST /api/templates
    [HttpPost]
    public async Task<IActionResult> CreateTemplate(
        [FromBody] CreateTemplateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) ||
            string.IsNullOrWhiteSpace(request.WhatsAppTenantId) ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Body))
        {
            return BadRequest(new
            {
                error = "Tenant, WhatsApp account, name and body are required."
            });
        }

        if (request.Name.Length > 100 ||
            request.Body.Length > 4000)
        {
            return BadRequest(new { error = "Input exceeds allowed length." });
        }

        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == request.TenantId && x.IsActive,
                cancellationToken);

        if (tenant is null)
            return BadRequest(new { error = "Tenant not found or inactive." });

        var whatsappAccount = await _db.WhatsAppTenants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == request.WhatsAppTenantId
                     && x.TenantId == request.TenantId
                     && x.IsActive,
                cancellationToken);

        if (whatsappAccount is null)
        {
            return BadRequest(new
            {
                error = "WhatsApp account does not belong to this tenant."
            });
        }

        var name = request.Name.Trim();

        var exists = await _db.WhatsAppTemplates.AnyAsync(
            x => x.TenantId == request.TenantId
                 && x.WhatsAppTenantId == request.WhatsAppTenantId
                 && x.Name == name,
            cancellationToken);

        if (exists)
            return Conflict(new { error = "Template name already exists." });

        var category = request.Category?.ToUpperInvariant();

        if (category is not ("UTILITY" or "MARKETING" or "AUTHENTICATION"))
            return BadRequest(new { error = "Invalid category." });

        var template = new WhatsAppTemplateEntity
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = request.TenantId,
            WhatsAppTenantId = request.WhatsAppTenantId,
            Name = name,
            Category = category,
            Language = string.IsNullOrWhiteSpace(request.Language)
                ? "ar"
                : request.Language.Trim(),
            Body = request.Body.Trim(),
            Status = "LOCAL",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.WhatsAppTemplates.Add(template);

        await _db.SaveChangesAsync(cancellationToken);

        return Created(
            $"/api/templates/{template.Id}",
            new
            {
                message = "Local template created successfully.",
                template
            });
    }

    // GET /api/templates/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetTemplate(
        string id,
        [FromQuery] string tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "tenantId is required" });

        var template = await _db.WhatsAppTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == id && x.TenantId == tenantId,
                cancellationToken);

        return template is null
            ? NotFound()
            : Ok(template);
    }

    // DELETE /api/templates/{id}?tenantId=...
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTemplate(
        string id,
        [FromQuery] string tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "tenantId is required" });

        var template = await _db.WhatsAppTemplates
            .FirstOrDefaultAsync(
                x => x.Id == id && x.TenantId == tenantId,
                cancellationToken);

        if (template is null)
            return NotFound();

        template.IsActive = false;
        template.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { message = "Template deactivated successfully." });
    }
}
