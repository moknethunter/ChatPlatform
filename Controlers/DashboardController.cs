using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController, Route("api/dashboard")]
[Authorize(Policy=PlatformSecurity.ReadPolicy)]
public sealed class DashboardController(PlatformDbContext db):ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var tenant=User.TenantId();
        var today=DateTime.UtcNow.Date;
        var c=db.WhatsAppConversations.AsNoTracking().Where(x=>x.TenantId==tenant);
        var m=db.WhatsAppMessages.AsNoTracking().Where(x=>x.TenantId==tenant && x.TimestampUtc>=today);
        var metrics=new {
            contacts=await db.CrmContacts.CountAsync(x=>x.TenantId==tenant,ct),
            openConversations=await c.CountAsync(x=>x.Status=="open",ct),
            unreadMessages=await c.SumAsync(x=>(long)x.UnreadCount,ct),
            incomingToday=await m.CountAsync(x=>x.Direction=="Inbound",ct),
            outgoingToday=await m.CountAsync(x=>x.Direction=="Outbound",ct)
        };
        var recent=await c.OrderByDescending(x=>x.UpdatedAtUtc).Take(8)
            .Select(x=>new {x.Id,x.ContactPhone,x.Status,x.UnreadCount,x.UpdatedAtUtc,
                name=db.CrmContacts.Where(p=>p.TenantId==tenant && p.Phone==x.ContactPhone).Select(p=>p.DisplayName).FirstOrDefault()})
            .ToListAsync(ct);
        return Ok(new {metrics,recent,asOfUtc=DateTime.UtcNow,dayBoundary="UTC"});
    }
}
