using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Subscriptions;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace ChatPlatform.Controllers;

[ApiController,Route("api/subscription"),Authorize(Policy=PlatformSecurity.AdminPolicy)]
public sealed class SubscriptionController(PlatformDbContext db):ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var tenant=User.TenantId();var now=DateTime.UtcNow;var access=await Entitlements.ReadAsync(db,tenant,now,ct);
        return Ok(new{access.Managed,access.Active,access.Plan,access.EndsAtUtc,usage=new{
            messages=await Entitlements.ReservedAsync(db,tenant,now,ct),
            agents=await db.ManagedAgents.CountAsync(x=>x.TenantId==tenant&&x.IsActive,ct),
            accounts=await db.WhatsAppTenants.CountAsync(x=>x.TenantId==tenant&&x.IsActive,ct),
            bots=await db.BotDrafts.CountAsync(x=>x.TenantId==tenant,ct),
            articles=await db.KnowledgeArticles.CountAsync(x=>x.TenantId==tenant,ct)},period="UTC calendar month"});
    }
}
