using ChatPlatform.Infrastructure.Subscriptions;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/team")]
[Authorize(Policy = PlatformSecurity.AdminPolicy)]
public sealed class TeamController(PlatformDbContext db, IConfiguration configuration, ILogger<TeamController> logger) : ControllerBase
{
    public sealed record CreateAgentRequest(string? Username, string? DisplayName, string? Password);
    public sealed record ResetPasswordRequest(string? Password);
    public sealed record SetActiveRequest(bool? IsActive);

    private static bool ValidUsername(string? username) => username is { Length: >= 3 and <= 64 }
        && username.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static bool ValidPassword(string? password) => password is { Length: >= 14 and <= 256 }
        && !string.IsNullOrWhiteSpace(password);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenant = User.TenantId();
        return Ok(await db.ManagedAgents.AsNoTracking().Where(x => x.TenantId == tenant)
            .OrderBy(x => x.Username).Take(500).Select(x => new {
                x.Username, x.DisplayName, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc, role = "Agent"
            }).ToListAsync(ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateAgentRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var username = req.Username?.Trim();
        var displayName = req.DisplayName?.Trim();
        if (!ValidUsername(username) || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 100 || !ValidPassword(req.Password))
            return BadRequest(new { error = "invalid_agent", message = "Use a lowercase username (3–64 characters), a name and a password of 14–256 characters." });
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if (PlatformSecurity.IsReservedUsername(configuration, username!) ||
            await db.ManagedTenantAdmins.AnyAsync(x=>x.Username==username,ct) ||
            await db.ManagedAgents.AnyAsync(x => x.Username == username, ct))
            return Conflict(new { error = "username_unavailable" });
        var now = DateTime.UtcNow;
        if(await Entitlements.CapacityAsync(db,tenant,"agents",1,now,ct) is string denied)return Conflict(new{error=denied});
        var agent = new ManagedAgentEntity { Username = username!, TenantId = tenant, DisplayName = displayName,
            PasswordHash = PlatformSecurity.HashAgentPassword(req.Password!), CreatedAtUtc = now, UpdatedAtUtc = now };
        db.ManagedAgents.Add(agent);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.ManagedAgents.AnyAsync(x => x.Username == username, ct)) return Conflict(new { error = "username_unavailable" });
            throw;
        }
        await tx.CommitAsync(ct);
        logger.LogInformation("Agent created: actor={Actor} tenant={Tenant} agent={Agent}", User.Identity?.Name, tenant, username);
        return StatusCode(201, new { agent.Username, agent.DisplayName, agent.IsActive, role = "Agent" });
    }

    [HttpPut("{username}/password")]
    public async Task<IActionResult> ResetPassword(string username, ResetPasswordRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        if (!ValidPassword(req.Password)) return BadRequest(new { error = "invalid_password" });
        var agent = await db.ManagedAgents.SingleOrDefaultAsync(x => x.Username == username && x.TenantId == tenant, ct);
        if (agent == null) return NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        agent.PasswordHash = PlatformSecurity.HashAgentPassword(req.Password!);
        agent.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await db.PlatformSessions.Where(x => x.Username == username && x.TenantId == tenant).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Agent password reset: actor={Actor} tenant={Tenant} agent={Agent}", User.Identity?.Name, tenant, username);
        return NoContent();
    }

    [HttpPut("{username}/active")]
    public async Task<IActionResult> SetActive(string username, SetActiveRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        if(req.IsActive==null)return BadRequest();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var agent = await db.ManagedAgents.SingleOrDefaultAsync(x => x.Username == username && x.TenantId == tenant, ct);
        if (agent == null) return NotFound();
        if(req.IsActive.Value&&!agent.IsActive&&await Entitlements.CapacityAsync(db,tenant,"agents",1,DateTime.UtcNow,ct) is string denied)return Conflict(new{error=denied});
        agent.IsActive = req.IsActive.Value;
        agent.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Re-enabling must never revive a previously issued session.
        await db.PlatformSessions.Where(x => x.Username == username && x.TenantId == tenant).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Agent activity changed: actor={Actor} tenant={Tenant} agent={Agent} active={Active}", User.Identity?.Name, tenant, username, req.IsActive);
        return NoContent();
    }
}
