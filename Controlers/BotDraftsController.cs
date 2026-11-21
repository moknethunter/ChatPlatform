using ChatPlatform.Infrastructure.Subscriptions;
using System.Text.Json;
using ChatPlatform.Infrastructure.Bots;
using static ChatPlatform.Infrastructure.Bots.BotFlow;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController, Route("api/bots")]
[Authorize(Policy = PlatformSecurity.AdminPolicy)]
[RequestSizeLimit(65536)]
public sealed class BotDraftsController(PlatformDbContext db) : ControllerBase
{
    public record SaveRequest(string? Name, Definition? Flow, int Version);
    public record SimulateRequest(Definition? Flow, string? Input, string[]? Inputs = null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenant = User.TenantId();
        return Ok(await db.BotDrafts.AsNoTracking().Where(x => x.TenantId == tenant).OrderByDescending(x => x.UpdatedAtUtc)
            .Take(100).Select(x => new { x.Id, x.Name, x.Version, x.UpdatedAtUtc }).ToListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var row = await db.BotDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct);
        if (row == null) return NotFound();
        return Ok(new { row.Id, row.Name, row.Version, row.UpdatedAtUtc, flow = JsonSerializer.Deserialize<Definition>(row.DefinitionJson, Json) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(SaveRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var error = Validate(req.Flow);
        if (error != null || string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100) return BadRequest(new { error = error ?? "invalid_name" });
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(await Entitlements.CapacityAsync(db,tenant,"bots",1,DateTime.UtcNow,ct) is string denied)return Conflict(new{error=denied});
        if (await db.BotDrafts.CountAsync(x => x.TenantId == tenant, ct) >= 100) return Conflict(new { error = "draft_limit" });
        var row = new BotDraftEntity { TenantId = tenant, Name = req.Name.Trim(), DefinitionJson = JsonSerializer.Serialize(req.Flow, Json), UpdatedAtUtc = DateTime.UtcNow };
        db.BotDrafts.Add(row); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return StatusCode(201, new { row.Id, row.Name, row.Version });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, SaveRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var query = db.BotDrafts.Where(x => x.Id == id && x.TenantId == tenant);
        if (!await query.AnyAsync(ct)) return NotFound();
        var error = Validate(req.Flow);
        if (error != null || string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100) return BadRequest(new { error = error ?? "invalid_name" });
        if (req.Version < 1 || req.Version == int.MaxValue) return BadRequest();
        var serialized = JsonSerializer.Serialize(req.Flow, Json);
        var name = req.Name.Trim(); var now = DateTime.UtcNow;
        var updated = await query.Where(x => x.Version == req.Version).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, name)
            .SetProperty(x => x.DefinitionJson, serialized).SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAtUtc, now), ct);
        return updated == 0 ? Conflict(new { error = "stale_version" }) : Ok(new { id, name, version = req.Version + 1 });
    }

    [HttpPost("simulate")]
    public IActionResult Simulate(SimulateRequest req)
    {
        var error = Validate(req.Flow);
        if (error != null || (req.Input?.Length ?? 0) > 4000) return BadRequest(new { error = error ?? "input_too_long" });
        if(req.Inputs==null)return Ok(Evaluate(req.Flow!,req.Input??""));
        if(req.Inputs.Length is <1 or >12 || req.Inputs.Any(x=>x==null||x.Length>4000))return BadRequest(new{error="invalid_turns"});
        var turns=new List<Evaluation>();string? next=null;
        foreach(var input in req.Inputs)
        {
            var result=Evaluate(req.Flow!,input,next);turns.Add(result);next=result.NextNode;
            if(result.Handoff||next==null)break;
        }
        return Ok(new{turns,nextNode=next});
    }

    public record PublishRequest(int Version);

    [HttpGet("active")]
    public async Task<IActionResult> Active(CancellationToken ct)
    {
        var tenant = User.TenantId();
        var active = await db.BotReleases.AsNoTracking().Where(x => x.TenantId == tenant)
            .Select(x => new { x.DraftId, x.Name, x.Version, x.ActivationId, x.PublishedAtUtc }).SingleOrDefaultAsync(ct);
        return Ok(new { active });
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, PublishRequest req, CancellationToken ct)
    {
        var tenant = User.TenantId();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if(await Entitlements.CapacityAsync(db,tenant,"send",0,DateTime.UtcNow,ct) is string denied)return Conflict(new{error=denied});
        var draft = await db.BotDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenant, ct);
        if (draft == null) return NotFound();
        if (draft.Version != req.Version) return Conflict(new { error = "stale_version" });
        var definition = JsonSerializer.Deserialize<Definition>(draft.DefinitionJson, Json);
        var error = Validate(definition);
        if (error != null) return BadRequest(new { error });
        var replies = definition!.Nodes!.Where(n => n.Type is "reply" or "wait").ToArray();
        if ((replies.Length == 0 && !definition.Nodes!.Any(n=>n.Type=="handoff")) || replies.Length > 12 || replies.Sum(n => n.Text!.Length) + 2 * Math.Max(0,replies.Length - 1) > 4000)
            return BadRequest(new { error = "publish_reply_limit" });
        var row = await db.BotReleases.SingleOrDefaultAsync(x => x.TenantId == tenant, ct);
        if (row == null) { row = new BotReleaseEntity { TenantId = tenant }; db.BotReleases.Add(row); }
        row.DraftId = draft.Id; row.Name = draft.Name; row.Version = draft.Version; row.DefinitionJson = draft.DefinitionJson;
        row.PublishedAtUtc = DateTime.UtcNow; row.ActivationId = Guid.NewGuid();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Ok(new { row.DraftId, row.Name, row.Version, row.ActivationId, row.PublishedAtUtc });
    }

    [HttpDelete("{id:guid}/publish/{activationId:guid}")]
    public async Task<IActionResult> Stop(Guid id, Guid activationId, CancellationToken ct)
    {
        var tenant = User.TenantId();
        var affected = await db.BotReleases.Where(x => x.TenantId == tenant && x.DraftId == id && x.ActivationId == activationId).ExecuteDeleteAsync(ct);
        return affected == 0 ? NotFound() : NoContent();
    }

    [HttpGet("runs")]
    public async Task<IActionResult> Runs(int limit = 50, CancellationToken ct = default)
    {
        var tenant = User.TenantId();
        return Ok(await db.AutomationRuns.AsNoTracking().Where(r => r.TenantId == tenant).OrderByDescending(r => r.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100)).Select(r => new { r.InboundMessageId, r.ConversationId, r.BotId, r.BotVersion,
                r.RuleId, r.Outcome, r.OutboundMessageId, r.CreatedAtUtc }).ToListAsync(ct));
    }
}
