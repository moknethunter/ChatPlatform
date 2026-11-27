using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Infrastructure.Subscriptions;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace ChatPlatform.Controllers;

[ApiController,Route("api/platform-admin"),Authorize(Policy=PlatformSecurity.SuperPolicy),RequestSizeLimit(32768)]
public sealed class PlatformAdminController(PlatformDbContext db,IConfiguration config):ControllerBase
{
    public record PlanRequest(string? Name,int MonthlyMessages,int Agents,int Accounts,int Bots,int Articles,bool Campaigns,bool OwnModel,long MonthlyPriceMinor,string? Currency);
    public record NewTenantRequest(string? Name,string? Username,string? Password,Guid PlanId,DateTimeOffset EndsAt);
    public record SubscriptionRequest(Guid PlanId,bool? Enabled,DateTimeOffset EndsAt,int Version);
    public record ActiveRequest(bool? IsActive);
    public record PasswordRequest(string? Password);
    private string Actor=>User.Identity!.Name??"unknown";
    private void Audit(string action,string target,string detail)=>db.PlatformAdminAudit.Add(new PlatformAdminAuditEntity{Actor=Actor,Action=action,Target=target,Detail=detail});
    private static bool ValidEnd(DateTimeOffset at)=>at.UtcDateTime>DateTime.UtcNow&&at.UtcDateTime<=DateTime.UtcNow.AddYears(3);
    private static bool ValidUser(string? name)=>name is {Length:>=3 and <=64}&&name.All(c=>c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static bool ValidPassword(string? pass)=>pass is {Length:>=14 and <=256}&&!string.IsNullOrWhiteSpace(pass);
    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct)=>Ok(await db.ServicePlans.AsNoTracking().OrderBy(x=>x.Name).Take(200).ToListAsync(ct));
    [HttpPost("plans")]
    public async Task<IActionResult> CreatePlan(PlanRequest r,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>100||r.MonthlyMessages is <0 or >1000000||r.Agents is <0 or >500||r.Accounts is <0 or >100||r.Bots is <0 or >100||r.Articles is <0 or >500||
            r.MonthlyPriceMinor is <0 or >100000000||r.Currency is not ("USD" or "EUR" or "SAR" or "YER"))return BadRequest(new{error="invalid_plan"});
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(await db.ServicePlans.CountAsync(ct)>=200)return Conflict(new{error="plan_limit"});
        var p=new ServicePlanEntity{Name=r.Name.Trim(),MonthlyMessages=r.MonthlyMessages,Agents=r.Agents,Accounts=r.Accounts,Bots=r.Bots,Articles=r.Articles,Campaigns=r.Campaigns,OwnModel=r.OwnModel,MonthlyPriceMinor=r.MonthlyPriceMinor,Currency=r.Currency};
        db.ServicePlans.Add(p);Audit("plan_created",p.Id.ToString(),p.Name);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return StatusCode(201,p);
    }
    [HttpGet("tenants")]
    public async Task<IActionResult> Tenants(int page=1,CancellationToken ct=default)
    {
        page=Math.Clamp(page,1,10000);
        return Ok(await db.Tenants.AsNoTracking().OrderBy(t=>t.Id).Skip((page-1)*100).Take(100).Select(t=>new{
            t.Id,t.Name,t.IsActive,t.CreatedAtUtc,
            subscription=db.TenantSubscriptions.Where(s=>s.TenantId==t.Id).Select(s=>new{s.PlanId,s.Enabled,s.EndsAtUtc,s.Version}).FirstOrDefault(),
            administrators=db.ManagedTenantAdmins.Where(a=>a.TenantId==t.Id).Select(a=>a.Username).ToList()
        }).ToListAsync(ct));
    }
    // Indexed keyset navigation, independent of the number of preceding rows.
    // Preserve the old page-number endpoint for existing API clients.
    [HttpGet("tenants/browse")]
    public async Task<IActionResult> BrowseTenants(string? after, string? search, string? by, int limit=100, CancellationToken ct=default)
    {
        if ((after?.Length ?? 0)>128 || (search?.Length ?? 0)>100 || limit is <1 or >100 ||
            (by!=null && by is not ("id" or "name" or "username")))
            return BadRequest(new { error="invalid_browse" });
        var q = string.IsNullOrEmpty(after)
            ? db.Tenants.AsNoTracking()
            : db.Tenants.FromSqlInterpolated($"SELECT * FROM Tenants WHERE Id > {after}").AsNoTracking();
        search=search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            q = by switch
            {
                "name" => q.Where(t=>t.Name==search),
                "username" => q.Where(t=>db.ManagedTenantAdmins.Any(a=>a.Username==search && a.TenantId==t.Id)),
                _ => q.Where(t=>t.Id==search)
            };
        }
        var items=await q.OrderBy(t=>t.Id).Take(limit+1).Select(t=>new{
            t.Id,t.Name,t.IsActive,t.CreatedAtUtc,
            subscription=db.TenantSubscriptions.Where(s=>s.TenantId==t.Id).Select(s=>new{s.PlanId,s.Enabled,s.EndsAtUtc,s.Version}).FirstOrDefault(),
            administrators=db.ManagedTenantAdmins.Where(a=>a.TenantId==t.Id).OrderBy(a=>a.Username).Select(a=>a.Username).ToList()
        }).ToListAsync(ct);
        var hasMore=items.Count>limit;
        if(hasMore)items.RemoveAt(items.Count-1);
        Response.Headers["Cache-Control"]="no-store";
        return Ok(new{items,nextCursor=hasMore?items[^1].Id:null});
    }

    [HttpPost("tenants")]
    public async Task<IActionResult> CreateTenant(NewTenantRequest r,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>100||!ValidUser(r.Username)||!ValidPassword(r.Password)||!ValidEnd(r.EndsAt))return BadRequest(new{error="invalid_tenant"});
        // Password derivation is CPU-heavy; keep it outside the database write transaction.
        if(PlatformSecurity.IsReservedUsername(config,r.Username!))return Conflict(new{error="username_unavailable"});
        var passwordHash=PlatformSecurity.HashAgentPassword(r.Password!);
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(PlatformSecurity.IsReservedUsername(config,r.Username!)||await db.ManagedAgents.AnyAsync(a=>a.Username==r.Username,ct)||await db.ManagedTenantAdmins.AnyAsync(a=>a.Username==r.Username,ct))return Conflict(new{error="username_unavailable"});
        if(!await db.ServicePlans.AnyAsync(p=>p.Id==r.PlanId,ct))return NotFound();
        var t=new TenantEntity{Name=r.Name.Trim()};
        db.Tenants.Add(t);db.ManagedTenantAdmins.Add(new ManagedTenantAdminEntity{TenantId=t.Id,Username=r.Username!,PasswordHash=passwordHash});
        db.TenantSubscriptions.Add(new TenantSubscriptionEntity{TenantId=t.Id,PlanId=r.PlanId,EndsAtUtc=r.EndsAt.UtcDateTime});
        Audit("tenant_created",t.Id,t.Name);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        return StatusCode(201,new{t.Id,t.Name,username=r.Username});
    }
    [HttpPut("tenants/{id}/subscription")]
    public async Task<IActionResult> Assign(string id,SubscriptionRequest r,CancellationToken ct)
    {
        if(r.Enabled==null||r.Version<0||r.Version==int.MaxValue||(r.Enabled.Value&&!ValidEnd(r.EndsAt)))return BadRequest();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(!await db.Tenants.AnyAsync(t=>t.Id==id,ct)||!await db.ServicePlans.AnyAsync(p=>p.Id==r.PlanId,ct))return NotFound();
        var s=await db.TenantSubscriptions.SingleOrDefaultAsync(x=>x.TenantId==id,ct);
        if(s==null){if(r.Version!=0)return Conflict(new{error="stale_version"});s=new TenantSubscriptionEntity{TenantId=id};db.TenantSubscriptions.Add(s);}
        else{if(s.Version!=r.Version)return Conflict(new{error="stale_version"});s.Version++;}
        s.PlanId=r.PlanId;s.Enabled=r.Enabled.Value;s.EndsAtUtc=r.EndsAt.UtcDateTime;s.UpdatedAtUtc=DateTime.UtcNow;
        if(!s.Enabled)await ChatPlatform.Infrastructure.Marketing.Consent.CancelAsync(db,id,db.WhatsAppOutbox.Where(o=>o.TenantId==id),DateTime.UtcNow,"subscription_suspended",ct);
        Audit("subscription_changed",id,$"{r.PlanId} enabled={r.Enabled} until={s.EndsAtUtc:O}");
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Ok(s);
    }
    [HttpPut("tenants/{id}/active")]
    public async Task<IActionResult> Active(string id,ActiveRequest r,CancellationToken ct)
    {
        if(r.IsActive==null)return BadRequest();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var t=await db.Tenants.SingleOrDefaultAsync(x=>x.Id==id,ct);if(t==null)return NotFound();
        t.IsActive=r.IsActive.Value;
        if(!t.IsActive)await ChatPlatform.Infrastructure.Marketing.Consent.CancelAsync(db,id,db.WhatsAppOutbox.Where(o=>o.TenantId==id),DateTime.UtcNow,"tenant_suspended",ct);
        await db.PlatformSessions.Where(s=>s.TenantId==id).ExecuteDeleteAsync(ct);
        Audit("tenant_activity",id,t.IsActive.ToString());await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return NoContent();
    }
    [HttpPut("tenants/{id}/admins/{username}/password")]
    public async Task<IActionResult> Reset(string id,string username,PasswordRequest r,CancellationToken ct)
    {
        if(!ValidPassword(r.Password))return BadRequest();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var a=await db.ManagedTenantAdmins.SingleOrDefaultAsync(x=>x.TenantId==id&&x.Username==username,ct);if(a==null)return NotFound();
        a.PasswordHash=PlatformSecurity.HashAgentPassword(r.Password!);
        await db.PlatformSessions.Where(s=>s.TenantId==id&&s.Username==username).ExecuteDeleteAsync(ct);
        Audit("tenant_admin_password_reset",id,username);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return NoContent();
    }
    [HttpGet("audit")]
    public async Task<IActionResult> AuditList(CancellationToken ct)=>Ok(await db.PlatformAdminAudit.AsNoTracking().OrderByDescending(a=>a.CreatedAtUtc).Take(100).ToListAsync(ct));
}
