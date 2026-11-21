using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace ChatPlatform.Controllers;

[ApiController,Route("api/reports"),Authorize(Policy=PlatformSecurity.AdminPolicy)]
public sealed class ReportsController(PlatformDbContext db):ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int days=7,CancellationToken ct=default)
    {
        var tenant=User.TenantId();days=Math.Clamp(days,1,30);
        var today=DateTime.UtcNow.Date;var from=today.AddDays(1-days);var end=today.AddDays(1);
        var messages=db.WhatsAppMessages.AsNoTracking().Where(m=>m.TenantId==tenant&&m.TimestampUtc>=from&&m.TimestampUtc<end);
        var raw=await messages.GroupBy(m=>new {Day=m.TimestampUtc.Date,m.Direction}).Select(g=>new{g.Key.Day,g.Key.Direction,count=g.Count()}).ToListAsync(ct);
        var daily=Enumerable.Range(0,days).Select(i=>{var day=from.AddDays(i);return new {day,inbound=raw.Where(x=>x.Day==day&&x.Direction=="Inbound").Sum(x=>x.count),outbound=raw.Where(x=>x.Day==day&&x.Direction=="Outbound").Sum(x=>x.count)};}).ToArray();
        var outbox=await db.WhatsAppOutbox.AsNoTracking().Where(o=>o.TenantId==tenant&&o.CreatedAtUtc>=from&&o.CreatedAtUtc<end).GroupBy(o=>o.Status).Select(g=>new{status=g.Key,count=g.Count()}).ToListAsync(ct);
        var delivery=await messages.Where(m=>m.Direction=="Outbound").GroupBy(m=>m.Status).Select(g=>new{status=g.Key,count=g.Count()}).ToListAsync(ct);
        var consent=await db.MarketingConsents.AsNoTracking().Where(c=>c.TenantId==tenant).GroupBy(c=>c.Granted).Select(g=>new{granted=g.Key,count=g.Count()}).ToListAsync(ct);
        var bot=await db.AutomationRuns.AsNoTracking().Where(r=>r.TenantId==tenant&&r.CreatedAtUtc>=from&&r.CreatedAtUtc<end).GroupBy(r=>r.Outcome).Select(g=>new{outcome=g.Key,count=g.Count()}).ToListAsync(ct);
        return Ok(new{fromUtc=from,toUtcExclusive=end,daily,outbox,delivery,consent,bot,timezone="UTC"});
    }
}