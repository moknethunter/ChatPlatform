using ChatPlatform.Infrastructure.AI;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController, Route("api/knowledge")]
[Authorize(Policy = PlatformSecurity.AdminPolicy)]
[RequestSizeLimit(65536)]
public sealed class KnowledgeController(PlatformDbContext db, OwnModelClient model) : ControllerBase
{
    public record ArticleRequest(string? Title, string? Body, bool IsPublished, int Version);
    public record QuestionRequest(string? Question);
    private static bool Valid(ArticleRequest r) => !string.IsNullOrWhiteSpace(r.Title) && r.Title.Length <= 150 &&
        !string.IsNullOrWhiteSpace(r.Body) && r.Body.Length <= 8000;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenant = User.TenantId();
        return Ok(await db.KnowledgeArticles.AsNoTracking().Where(x=>x.TenantId==tenant)
            .OrderByDescending(x=>x.UpdatedAtUtc).Take(500)
            .Select(x=>new { x.Id,x.Title,x.IsPublished,x.Version,x.UpdatedAtUtc }).ToListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id,CancellationToken ct)
    {
        var tenant=User.TenantId();
        var item=await db.KnowledgeArticles.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id && x.TenantId==tenant,ct);
        return item==null?NotFound():Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create(ArticleRequest req,CancellationToken ct)
    {
        if(!Valid(req))return BadRequest(new {error="invalid_article"});
        var tenant=User.TenantId();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(await db.KnowledgeArticles.CountAsync(x=>x.TenantId==tenant,ct)>=500)return Conflict(new {error="knowledge_limit"});
        var item=new KnowledgeArticleEntity {TenantId=tenant,Title=req.Title!.Trim(),Body=req.Body!.Trim(),IsPublished=req.IsPublished,UpdatedAtUtc=DateTime.UtcNow};
        db.KnowledgeArticles.Add(item);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        return StatusCode(201,item);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id,ArticleRequest req,CancellationToken ct)
    {
        var tenant=User.TenantId();
        var q=db.KnowledgeArticles.Where(x=>x.Id==id && x.TenantId==tenant);
        if(!await q.AnyAsync(ct))return NotFound();
        if(!Valid(req)||req.Version<1||req.Version==int.MaxValue)return BadRequest(new {error="invalid_article"});
        var title=req.Title!.Trim();var body=req.Body!.Trim();var now=DateTime.UtcNow;
        var n=await q.Where(x=>x.Version==req.Version).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Title,title)
            .SetProperty(x=>x.Body,body).SetProperty(x=>x.IsPublished,req.IsPublished)
            .SetProperty(x=>x.Version,x=>x.Version+1).SetProperty(x=>x.UpdatedAtUtc,now),ct);
        return n==0?Conflict(new {error="stale_version"}):Ok(new {id,version=req.Version+1});
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id,int version,CancellationToken ct)
    {
        var tenant=User.TenantId();
        var q=db.KnowledgeArticles.Where(x=>x.Id==id && x.TenantId==tenant);
        if(!await q.AnyAsync(ct))return NotFound();
        return await q.Where(x=>x.Version==version).ExecuteDeleteAsync(ct)==1?NoContent():Conflict(new {error="stale_version"});
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search(QuestionRequest req,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(req.Question)||req.Question.Length>2000)return BadRequest();
        return Ok(await KnowledgeSearch.FindAsync(db,User.TenantId(),req.Question,ct));
    }

    [HttpGet("model")]
    public IActionResult ModelStatus()=>Ok(new {provider="own-model",configured=model.IsConfigured,protocol="chatplatform.own-model.v1"});

    [HttpPost("preview")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("own-model")]
    public async Task<IActionResult> Preview(QuestionRequest req,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(req.Question)||req.Question.Length>2000)return BadRequest();
        if(!model.IsConfigured)return Conflict(new {error="own_model_not_configured"});
        var knowledge=await KnowledgeSearch.FindAsync(db,User.TenantId(),req.Question,ct);
        try
        {
            var answer=await model.AskAsync(req.Question,knowledge,ct);
            return answer==null?StatusCode(502,new {error="own_model_unavailable"}):Ok(new {answer,sources=knowledge.Select(x=>new {x.Id,x.Title})});
        }
        catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or FormatException)
        {return StatusCode(502,new {error="own_model_unavailable"});}
    }
}
