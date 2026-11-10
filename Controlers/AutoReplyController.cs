ChatPlatform
Repository navigation
Code
Issues
ChatPlatform/Controllers
/AutoReplyController.cs
moknethunter
moknethunter
18 hours ago
115 lines (100 loc) · 4.04 KB

Code

Blame
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/autoreply")]
public class AutoReplyController : ControllerBase
{
    private readonly PlatformDbContext _db;
    public AutoReplyController(PlatformDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rules = await _db.AutoReplyRules
            .AsNoTracking()
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);
        return Ok(rules);
    }

    public record RuleRequest(
        string Name,
        string MatchType,
        string? KeywordPattern,
        string ReplyText,
        int? Priority,
        int? CooldownSeconds,
        bool? IsActive);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RuleRequest req, CancellationToken ct)
    {
        var error = Validate(req);
        if (error != null) return BadRequest(new { error });

        var rule = new AutoReplyRuleEntity
        {
            TenantId = "default",
            Name = req.Name.Trim(),
            MatchType = req.MatchType.ToLowerInvariant(),
            KeywordPattern = req.KeywordPattern?.Trim(),
            ReplyText = req.ReplyText.Trim(),
            Priority = req.Priority ?? 100,
            CooldownSeconds = req.CooldownSeconds is > 0 ? req.CooldownSeconds.Value : 60,
            IsActive = req.IsActive ?? true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.AutoReplyRules.Add(rule);
        await _db.SaveChangesAsync(ct);
        return Ok(rule);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] RuleRequest req, CancellationToken ct)
    {
        var error = Validate(req);
        if (error != null) return BadRequest(new { error });

        var rule = await _db.AutoReplyRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule == null) return NotFound();

        rule.Name = req.Name.Trim();
        rule.MatchType = req.MatchType.ToLowerInvariant();
        rule.KeywordPattern = req.KeywordPattern?.Trim();
        rule.ReplyText = req.ReplyText.Trim();
        rule.Priority = req.Priority ?? rule.Priority;
        rule.CooldownSeconds = req.CooldownSeconds is > 0 ? req.CooldownSeconds.Value : rule.CooldownSeconds;
        if (req.IsActive.HasValue) rule.IsActive = req.IsActive.Value;
        rule.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(rule);
    }

    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        var rule = await _db.AutoReplyRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule == null) return NotFound();
        rule.IsActive = !rule.IsActive;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(rule);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var rule = await _db.AutoReplyRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule == null) return NotFound();
        _db.AutoReplyRules.Remove(rule);
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = true });
    }

    private static string? Validate(RuleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return "Name is required";
        if (string.IsNullOrWhiteSpace(req.ReplyText)) return "ReplyText is required";

        var allowed = new[] { "any", "exact", "contains", "starts_with", "regex" };
        var type = (req.MatchType ?? "").ToLowerInvariant();
        if (!allowed.Contains(type)) return $"MatchType must be one of: {string.Join(", ", allowed)}";
        if (type != "any" && string.IsNullOrWhiteSpace(req.KeywordPattern))
            return "KeywordPattern is required for this MatchType";

        return null;
    }
}

