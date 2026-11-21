using System.Text.Json;
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
    public record Node(string? Id, string? Type, string? Text, string? Next, string? Otherwise, int X, int Y);
    public record Definition(string? Start, Node[]? Nodes);
    public record SaveRequest(string? Name, Definition? Flow, int Version);
    public record SimulateRequest(Definition? Flow, string? Input);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static bool ValidId(string? id) => id is { Length: >= 1 and <= 40 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');

    private static string? Validate(Definition? flow)
    {
        if (flow?.Nodes is not { Length: >= 2 and <= 40 } nodes || !ValidId(flow.Start)) return "invalid_graph";
        var map = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (n == null || !ValidId(n.Id) || !map.TryAdd(n.Id!, n) || n.X is < 0 or > 3000 || n.Y is < 0 or > 3000)
                return "invalid_node";
            if (n.Type is not ("start" or "contains" or "reply" or "end")) return "invalid_type";
            if ((n.Text?.Length ?? 0) > 2000 || (n.Type is "contains" or "reply" && string.IsNullOrWhiteSpace(n.Text))) return "invalid_text";
            if (n.Type == "contains" && (!ValidId(n.Next) || !ValidId(n.Otherwise))) return "invalid_branch";
            if (n.Type is "start" or "reply" && (!ValidId(n.Next) || !string.IsNullOrEmpty(n.Otherwise))) return "invalid_next";
            if (n.Type == "end" && (!string.IsNullOrEmpty(n.Next) || !string.IsNullOrEmpty(n.Otherwise))) return "invalid_end";
        }
        if (!map.TryGetValue(flow.Start!, out var start) || start.Type != "start" || nodes.Count(n => n.Type == "start") != 1) return "invalid_start";
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        bool Walk(string? id)
        {
            if (string.IsNullOrEmpty(id)) return true;
            if (!map.TryGetValue(id, out var node) || visiting.Contains(id)) return false;
            if (visited.Contains(id)) return true;
            visiting.Add(id);
            if (!Walk(node.Next) || !Walk(node.Otherwise)) return false;
            visiting.Remove(id); visited.Add(id); return true;
        }
        return Walk(flow.Start) && visited.Count == nodes.Length ? null : "cycle_or_unreachable_node";
    }

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
        if (await db.BotDrafts.CountAsync(x => x.TenantId == tenant, ct) >= 100) return Conflict(new { error = "draft_limit" });
        var row = new BotDraftEntity { TenantId = tenant, Name = req.Name.Trim(), DefinitionJson = JsonSerializer.Serialize(req.Flow, Json), UpdatedAtUtc = DateTime.UtcNow };
        db.BotDrafts.Add(row); await db.SaveChangesAsync(ct);
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
        var map = req.Flow!.Nodes!.ToDictionary(n => n.Id!);
        var path = new List<string>(); var replies = new List<string>();
        string? cursor = req.Flow.Start;
        for (int i = 0; cursor != null && i < 40; i++)
        {
            var node = map[cursor]; path.Add(cursor);
            if (node.Type == "end") break;
            if (node.Type == "reply") replies.Add(node.Text!);
            cursor = node.Type == "contains" && !(req.Input ?? "").Contains(node.Text!, StringComparison.OrdinalIgnoreCase)
                ? node.Otherwise : node.Next;
        }
        // Draft simulation has no messaging, AI, HTTP or event-bus side effects.
        return Ok(new { path, replies });
    }
}
