using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
namespace ChatPlatform.Infrastructure.Marketing;

public static class Consent
{
    public static bool ValidPhone(string? phone)=>phone is {Length:>=7 and <=15} && phone[0]!='0' && phone.All(char.IsAsciiDigit);
    public static bool IsStop(string? text)=>text?.Trim().ToLowerInvariant() is "stop" or "unsubscribe" or "إلغاء الاشتراك" or "الغاء الاشتراك" or "إيقاف الرسائل";
    // Caller owns write transaction; never infer consent from an inbound conversation.
    public static async Task SetAsync(PlatformDbContext db,string tenant,string phoneId,string phone,bool granted,string evidence,string actor,DateTime now,CancellationToken ct)
    {
        var current=await db.MarketingConsents.SingleOrDefaultAsync(x=>x.TenantId==tenant&&x.PhoneNumberId==phoneId&&x.Phone==phone,ct);
        if(current==null){current=new MarketingConsentEntity{TenantId=tenant,PhoneNumberId=phoneId,Phone=phone};db.MarketingConsents.Add(current);}
        current.Granted=granted;current.Evidence=evidence;current.UpdatedBy=actor;current.UpdatedAtUtc=now;
        db.ConsentEvents.Add(new ConsentEventEntity{TenantId=tenant,PhoneNumberId=phoneId,Phone=phone,Granted=granted,Evidence=evidence,UpdatedBy=actor,UpdatedAtUtc=now});
        if(!granted)await CancelAsync(db,tenant,db.WhatsAppOutbox.Where(o=>o.TenantId==tenant&&o.PhoneNumberId==phoneId&&o.RecipientPhone==phone&&o.IdempotencyKey.StartsWith("campaign:")),now,"consent_revoked",ct);
    }
    public static async Task CancelAsync(PlatformDbContext db,string tenant,IQueryable<WhatsAppOutboxEntity> rows,DateTime now,string reason,CancellationToken ct)
    {
        var pending=rows.Where(o=>o.TenantId==tenant&&o.Status=="Pending");
        var ids=await pending.Where(o=>o.MessageId.HasValue).Select(o=>o.MessageId!.Value).ToListAsync(ct);
        await pending.ExecuteUpdateAsync(s=>s.SetProperty(o=>o.Status,"Cancelled").SetProperty(o=>o.LastError,reason).SetProperty(o=>o.UpdatedAtUtc,now),ct);
        if(ids.Count>0)await db.WhatsAppMessages.Where(m=>m.TenantId==tenant&&ids.Contains(m.Id)).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.Status,"Cancelled"),ct);
    }
}
