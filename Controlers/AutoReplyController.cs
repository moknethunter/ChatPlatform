using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/autoreply")]
public sealed class AutoReplyController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public AutoReplyController(PlatformDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? tenantId,
        CancellationToken ct)
    {
        var effectiveTenantId = NormalizeTenantId(tenantId);

        var rules = await _db.AutoReplyRules
            .AsNoTracking()
            .Where(r => r.TenantId == effectiveTenantId)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);

        return Ok(rules);
    }

    public sealed record RuleRequest(
        string Name,
        string MatchType,
        string? KeywordPattern,
        string ReplyText,
        int? Priority,
        int? CooldownSeconds,
        bool? IsActive,
        string? TenantId);

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] RuleRequest req,
        CancellationToken ct)
    {
        var error = Validate(req);

        if (error != null)
            return BadRequest(new { error });

        var now = DateTime.UtcNow;

        var rule = new AutoReplyRuleEntity
        {
            TenantId = NormalizeTenantId(req.TenantId),
            Name = req.Name.Trim(),
            MatchType = req.MatchType.Trim().ToLowerInvariant(),
            KeywordPattern = req.KeywordPattern?.Trim(),
            ReplyText = req.ReplyText.Trim(),
            Priority = Math.Clamp(
                req.Priority ?? 100,
                0,
                100000),
            CooldownSeconds = Math.Clamp(
                req.CooldownSeconds ?? 60,
                0,
                86400),
            IsActive = req.IsActive ?? true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.AutoReplyRules.Add(rule);

        await _db.SaveChangesAsync(ct);

        return Ok(rule);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] RuleRequest req,
        CancellationToken ct)
    {
        var error = Validate(req);

        if (error != null)
            return BadRequest(new { error });

        var tenantId = NormalizeTenantId(req.TenantId);

        var rule = await _db.AutoReplyRules
            .FirstOrDefaultAsync(
                r => r.Id == id &&
                     r.TenantId == tenantId,
                ct);

        if (rule == null)
            return NotFound();

        rule.Name = req.Name.Trim();
        rule.MatchType = req.MatchType
            .Trim()
            .ToLowerInvariant();

        rule.KeywordPattern =
            req.KeywordPattern?.Trim();

        rule.ReplyText =
            req.ReplyText.Trim();

        rule.Priority = Math.Clamp(
            req.Priority ?? rule.Priority,
            0,
            100000);

        rule.CooldownSeconds = Math.Clamp(
            req.CooldownSeconds ?? rule.CooldownSeconds,
            0,
            86400);

        if (req.IsActive.HasValue)
            rule.IsActive = req.IsActive.Value;

        rule.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(rule);
    }

    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(
        Guid id,
        [FromQuery] string? tenantId,
        CancellationToken ct)
    {
        var rule = await _db.AutoReplyRules
            .FirstOrDefaultAsync(
                r => r.Id == id &&
                     r.TenantId == NormalizeTenantId(tenantId),
                ct);

        if (rule == null)
            return NotFound();

        rule.IsActive = !rule.IsActive;
        rule.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(rule);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] string? tenantId,
        CancellationToken ct)
    {
        var rule = await _db.AutoReplyRules
            .FirstOrDefaultAsync(
                r => r.Id == id &&
                     r.TenantId == NormalizeTenantId(tenantId),
                ct);

        if (rule == null)
            return NotFound();

        _db.AutoReplyRules.Remove(rule);

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            deleted = true
        });
    }

    private static string NormalizeTenantId(
        string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return TenantEntity.DefaultTenantId;

        var value = tenantId.Trim();

        if (string.Equals(
                value,
                "default",
                StringComparison.OrdinalIgnoreCase))
        {
            return TenantEntity.DefaultTenantId;
        }

        return value;
    }

    private static string? Validate(
        RuleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return "اسم القاعدة مطلوب.";

        if (req.Name.Trim().Length > 100)
            return "اسم القاعدة طويل جدًا.";

        if (string.IsNullOrWhiteSpace(req.ReplyText))
            return "نص الرد مطلوب.";

        if (req.ReplyText.Trim().Length > 4096)
            return "نص الرد يتجاوز حد رسالة WhatsApp.";

        var allowed = new[]
        {
            "any",
            "exact",
            "contains",
            "starts_with",
            "regex"
        };

        var type = (req.MatchType ?? "")
            .Trim()
            .ToLowerInvariant();

        if (!allowed.Contains(type))
        {
            return
                $"نوع المطابقة يجب أن يكون واحدًا من: " +
                $"{string.Join(", ", allowed)}";
        }

        if (type != "any" &&
            string.IsNullOrWhiteSpace(req.KeywordPattern))
        {
            return "نمط البحث مطلوب لهذا النوع.";
        }

        if (type == "regex" &&
            req.KeywordPattern!.Length > 500)
        {
            return "تعبير Regex طويل جدًا.";
        }

        if (req.CooldownSeconds is < 0 or > 86400)
        {
            return
                "فترة الانتظار يجب أن تكون بين 0 و86400 ثانية.";
        }

        return null;
    }
}
