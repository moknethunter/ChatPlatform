using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
namespace ChatPlatform.Infrastructure.Subscriptions;

public static class Entitlements
{
    // Existing tenants without a subscription remain on their previous limits until explicitly migrated.
    public record Access(bool Managed,bool Active,ServicePlanEntity? Plan,DateTime? EndsAtUtc);
    public static async Task<Access> ReadAsync(PlatformDbContext db,string tenant,DateTime at,CancellationToken ct)
    {
        var s=await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x=>x.TenantId==tenant,ct);
        if(s==null)return new(false,true,null,null);
        var p=await db.ServicePlans.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==s.PlanId,ct);
        return new(true,s.Enabled&&s.EndsAtUtc>at&&p!=null,p,s.EndsAtUtc);
    }
    public static async Task<string?> CapacityAsync(PlatformDbContext db,string tenant,string resource,int additional,DateTime at,CancellationToken ct)
    {
        var access=await ReadAsync(db,tenant,at,ct);
        if(!access.Active)return "subscription_inactive";
        if(!access.Managed)return null;
        var p=access.Plan!;
        if(resource=="campaigns")return p.Campaigns?null:"feature_not_in_plan";
        if(resource=="own-model")return p.OwnModel?null:"feature_not_in_plan";
        if(resource=="send")return null;
        int used,limit;
        switch(resource)
        {
            case "agents":used=await db.ManagedAgents.CountAsync(x=>x.TenantId==tenant&&x.IsActive,ct);limit=p.Agents;break;
            case "accounts":used=await db.WhatsAppTenants.CountAsync(x=>x.TenantId==tenant&&x.IsActive,ct);limit=p.Accounts;break;
            case "bots":used=await db.BotDrafts.CountAsync(x=>x.TenantId==tenant,ct);limit=p.Bots;break;
            case "articles":used=await db.KnowledgeArticles.CountAsync(x=>x.TenantId==tenant,ct);limit=p.Articles;break;
            case "messages":used=await ReservedAsync(db,tenant,at,ct);limit=p.MonthlyMessages;break;
            default:throw new ArgumentOutOfRangeException(nameof(resource));
        }
        return (long)used+additional>limit?"quota_exceeded":null;
    }
    public static Task<int> ReservedAsync(PlatformDbContext db,string tenant,DateTime at,CancellationToken ct)
    {
        var first=new DateTime(at.Year,at.Month,1,0,0,0,DateTimeKind.Utc);var next=first.AddMonths(1);
        // A reservation counts once even if cancelled or failed; retrying the same row costs no extra quota.
        return db.WhatsAppOutbox.CountAsync(o=>o.TenantId==tenant&&(o.ScheduledForUtc??o.CreatedAtUtc)>=first&&(o.ScheduledForUtc??o.CreatedAtUtc)<next,ct);
    }
}
