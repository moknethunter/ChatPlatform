using ChatPlatform.Infrastructure.Subscriptions;
using System.Text.Json;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Marketing;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace ChatPlatform.Controllers;

[ApiController,Route("api/marketing"),Authorize(Policy=PlatformSecurity.AdminPolicy),RequestSizeLimit(65536)]
public sealed class MarketingController(PlatformDbContext db,IConfiguration config):ControllerBase
{
    public record ConsentRequest(string? PhoneNumberId,string? Phone,bool? Granted,string? Evidence);
    public record ListRequest(string? Name,string[]? Phones,int Version);
    public record ScheduleRequest(Guid Id,string? Name,Guid ListId,string? TemplateId,DateTimeOffset ScheduledAt);
    [HttpGet("consents")]
    public async Task<IActionResult> Consents(string phoneNumberId,CancellationToken ct)
    {
        var tenant=User.TenantId();
        if(!await db.WhatsAppTenants.AnyAsync(a=>a.TenantId==tenant&&a.PhoneNumberId==phoneNumberId,ct))return NotFound();
        return Ok(await db.MarketingConsents.AsNoTracking().Where(x=>x.TenantId==tenant&&x.PhoneNumberId==phoneNumberId).OrderByDescending(x=>x.UpdatedAtUtc).Take(500).ToListAsync(ct));
    }
    [HttpPut("consent")]
    public async Task<IActionResult> SetConsent(ConsentRequest req,CancellationToken ct)
    {
        var tenant=User.TenantId();
        if(!Consent.ValidPhone(req.Phone)||!WhatsAppMediaService.ValidId(req.PhoneNumberId)||req.Granted==null||string.IsNullOrWhiteSpace(req.Evidence)||req.Evidence.Length>500)return BadRequest(new{error="invalid_consent"});
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(!await db.WhatsAppTenants.AnyAsync(a=>a.TenantId==tenant&&a.PhoneNumberId==req.PhoneNumberId,ct)||!await db.CrmContacts.AnyAsync(c=>c.TenantId==tenant&&c.Phone==req.Phone,ct))return NotFound();
        await Consent.SetAsync(db,tenant,req.PhoneNumberId!,req.Phone!,req.Granted.Value,req.Evidence.Trim(),User.Identity!.Name??"unknown",DateTime.UtcNow,ct);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return NoContent();
    }
    // Prepare a snapshot of a CRM segment. No consent, list or campaign is modified.
    [HttpGet("segments/preview")]
    public async Task<IActionResult> PreviewSegment(string phoneNumberId, string? stage, string? tag, CancellationToken ct)
    {
        var tenant = User.TenantId();
        if (!WhatsAppMediaService.ValidId(phoneNumberId) ||
            (!string.IsNullOrEmpty(stage) && stage is not ("new" or "lead" or "customer" or "inactive")) ||
            (tag?.Length ?? 0) > 100)
            return BadRequest(new { error = "invalid_segment" });
        if (!await db.WhatsAppTenants.AnyAsync(a => a.TenantId == tenant && a.PhoneNumberId == phoneNumberId && a.IsActive, ct))
            return NotFound();
        var contacts = db.CrmContacts.AsNoTracking().Where(c => c.TenantId == tenant);
        if (!string.IsNullOrEmpty(stage)) contacts = contacts.Where(c => c.Stage == stage);
        tag = tag?.Trim();
        if (!string.IsNullOrEmpty(tag)) contacts = contacts.Where(c => c.Tags.Contains(tag));
        var total = await contacts.CountAsync(ct);
        var eligible = contacts.Where(c => db.MarketingConsents.Any(m => m.TenantId == tenant &&
            m.PhoneNumberId == phoneNumberId && m.Phone == c.Phone && m.Granted));
        var eligibleCount = await eligible.CountAsync(ct);
        var items = await eligible.OrderBy(c => c.Phone).Take(100)
            .Select(c => new { c.Phone, c.DisplayName, c.Stage, c.Tags }).ToListAsync(ct);
        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new { matched = total, eligible = eligibleCount, excluded = total - eligibleCount,
            truncated = eligibleCount > items.Count, items });
    }

    [HttpGet("lists")]
    public async Task<IActionResult> Lists(CancellationToken ct)
    {
        var tenant=User.TenantId();
        var rows=await db.CustomerLists.AsNoTracking().Where(x=>x.TenantId==tenant).OrderBy(x=>x.Name).Take(100).ToListAsync(ct);
        return Ok(rows.Select(x=>new{x.Id,x.Name,x.Version,phones=JsonSerializer.Deserialize<string[]>(x.PhonesJson)}));
    }
    [HttpPost("lists")]
    public Task<IActionResult> CreateList(ListRequest req,CancellationToken ct)=>SaveList(null,req,ct);
    [HttpPut("lists/{id:guid}")]
    public Task<IActionResult> UpdateList(Guid id,ListRequest req,CancellationToken ct)=>SaveList(id,req,ct);
    private async Task<IActionResult> SaveList(Guid? id,ListRequest req,CancellationToken ct)
    {
        var tenant=User.TenantId();
        if(string.IsNullOrWhiteSpace(req.Name)||req.Name.Length>100||req.Phones is not {Length:>=1 and <=100}||req.Phones.Any(p=>!Consent.ValidPhone(p)))return BadRequest(new{error="invalid_list"});
        var phones=req.Phones.Distinct().ToArray();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(await db.CrmContacts.CountAsync(c=>c.TenantId==tenant&&phones.Contains(c.Phone),ct)!=phones.Length)return BadRequest(new{error="unknown_contact"});
        CustomerListEntity row;
        if(id.HasValue){
            var old=await db.CustomerLists.SingleOrDefaultAsync(x=>x.TenantId==tenant&&x.Id==id,ct);
            if(old==null)return NotFound();if(req.Version!=old.Version||old.Version==int.MaxValue)return Conflict(new{error="stale_version"});
            row=old;row.Version++;
        }else{
            if(await db.CustomerLists.CountAsync(x=>x.TenantId==tenant,ct)>=100)return Conflict(new{error="list_limit"});
            row=new CustomerListEntity{TenantId=tenant};db.CustomerLists.Add(row);
        }
        row.Name=req.Name.Trim();row.PhonesJson=JsonSerializer.Serialize(phones);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Ok(new{row.Id,row.Name,row.Version,phones});
    }
    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        var tenant=User.TenantId();
        var access=await Entitlements.ReadAsync(db,tenant,DateTime.UtcNow,ct);
        var templates=await db.WhatsAppTemplates.AsNoTracking().Where(t=>t.TenantId==tenant&&t.IsActive&&t.Category=="MARKETING").Take(200).ToListAsync(ct);
        return Ok(new{enabled=config.GetValue<bool>("Campaigns:Enabled")&&access.Active&&(!access.Managed||access.Plan!.Campaigns),templates=templates.Where(TemplateApproval.Eligible).Select(t=>new{t.Id,t.Name,t.Language,t.Body,t.WhatsAppTenantId})});
    }
    [HttpPost("campaigns")]
    public async Task<IActionResult> Schedule(ScheduleRequest req,CancellationToken ct)
    {
        var tenant=User.TenantId();var now=DateTime.UtcNow;var date=req.ScheduledAt.UtcDateTime;
        if(req.Id==Guid.Empty||string.IsNullOrWhiteSpace(req.Name)||req.Name.Length>100||string.IsNullOrWhiteSpace(req.TemplateId)||req.TemplateId.Length>100)return BadRequest();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var old=await db.MarketingCampaigns.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==req.Id&&c.TenantId==tenant,ct);
        if(old!=null)return old.ListId==req.ListId&&old.TemplateId==req.TemplateId&&old.Name==req.Name.Trim()&&old.ScheduledAtUtc==date?Ok(new{old.Id,old.Status}):Conflict(new{error="idempotency_conflict"});
        if(date<now.AddMinutes(2)||date>now.AddDays(30))return BadRequest(new{error="invalid_schedule"});
        if(!config.GetValue<bool>("Campaigns:Enabled"))return Conflict(new{error="campaigns_disabled"});
        if(await db.MarketingCampaigns.AnyAsync(c=>c.Id==req.Id,ct))return Conflict();
        var list=await db.CustomerLists.AsNoTracking().SingleOrDefaultAsync(x=>x.TenantId==tenant&&x.Id==req.ListId,ct);
        var template=await db.WhatsAppTemplates.AsNoTracking().SingleOrDefaultAsync(x=>x.TenantId==tenant&&x.Id==req.TemplateId,ct);
        if(list==null||template==null)return NotFound();
        if(!TemplateApproval.Eligible(template))return BadRequest(new{error="ineligible_template"});
        var account=await db.WhatsAppTenants.AsNoTracking().SingleOrDefaultAsync(x=>x.TenantId==tenant&&x.Id==template.WhatsAppTenantId&&x.IsActive,ct);
        if(account==null||string.IsNullOrWhiteSpace(account.BusinessTokenEncrypted)||!WhatsAppMediaService.ValidId(account.WabaId))return Conflict(new{error="account_not_configured"});
        var phones=JsonSerializer.Deserialize<string[]>(list.PhonesJson)??[];
        var eligible=await db.MarketingConsents.AsNoTracking().Where(c=>c.TenantId==tenant&&c.PhoneNumberId==account.PhoneNumberId&&c.Granted&&phones.Contains(c.Phone)).Select(c=>c.Phone).OrderBy(p=>p).ToListAsync(ct);
        if(eligible.Count==0)return Conflict(new{error="no_consented_recipients"});
        if(await Entitlements.CapacityAsync(db,tenant,"campaigns",0,now,ct) is string disabled)return Conflict(new{error=disabled});
        if(await Entitlements.CapacityAsync(db,tenant,"campaigns",0,date.AddSeconds((eligible.Count-1)*5),ct) is string denied)return Conflict(new{error=denied});
        foreach(var month in Enumerable.Range(0,eligible.Count).Select(i=>date.AddSeconds(i*5)).GroupBy(d=>new{d.Year,d.Month}))
            if(await Entitlements.CapacityAsync(db,tenant,"messages",month.Count(),month.First(),ct) is string exceeded)return Conflict(new{error=exceeded});
        var day=date.Date;var end=day.AddDays(1);
        var reserved=await (from r in db.CampaignRecipients join c in db.MarketingCampaigns on r.CampaignId equals c.Id where r.TenantId==tenant&&c.TenantId==tenant&&c.ScheduledAtUtc>=day&&c.ScheduledAtUtc<end select r).CountAsync(ct);
        if(reserved+eligible.Count>300)return Conflict(new{error="daily_campaign_limit"});
        var campaign=new MarketingCampaignEntity{Id=req.Id,TenantId=tenant,Name=req.Name.Trim(),ListId=list.Id,TemplateId=template.Id,PhoneNumberId=account.PhoneNumberId,ScheduledAtUtc=date,CreatedAtUtc=now,CreatedBy=User.Identity!.Name??"unknown"};
        db.MarketingCampaigns.Add(campaign);
        for(int i=0;i<eligible.Count;i++){
            var phone=eligible[i];var due=date.AddSeconds(i*5);
            var conversation=await db.WhatsAppConversations.SingleOrDefaultAsync(c=>c.TenantId==tenant&&c.PhoneNumberId==account.PhoneNumberId&&c.ContactPhone==phone,ct);
            if(conversation==null){conversation=new WhatsAppConversationEntity{TenantId=tenant,PhoneNumberId=account.PhoneNumberId,ContactPhone=phone,CreatedAtUtc=now,UpdatedAtUtc=now};db.WhatsAppConversations.Add(conversation);}
            var payload=JsonSerializer.Serialize(new{messaging_product="whatsapp",recipient_type="individual",to=phone,type="template",template=new{name=template.Name,language=new{code=template.Language}}});
            var message=new WhatsAppMessageEntity{TenantId=tenant,ConversationId=conversation.Id,Direction="Outbound",MessageType="template",ContentJson=payload,TextPreview=template.Name,Status="Queued",TimestampUtc=now,CreatedAtUtc=now};
            var outbox=new WhatsAppOutboxEntity{TenantId=tenant,MessageId=message.Id,PhoneNumberId=account.PhoneNumberId,RecipientPhone=phone,MessageType="template",PayloadJson=payload,IdempotencyKey="campaign:"+campaign.Id.ToString("N")+":"+phone,Status="Pending",CreatedAtUtc=now,UpdatedAtUtc=now,NextAttemptAtUtc=due,ScheduledForUtc=due};
            db.WhatsAppMessages.Add(message);db.WhatsAppOutbox.Add(outbox);db.CampaignRecipients.Add(new CampaignRecipientEntity{TenantId=tenant,CampaignId=campaign.Id,Phone=phone,OutboxId=outbox.Id});
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Accepted(new{campaign.Id,campaign.Status,eligible=eligible.Count,excluded=phones.Length-eligible.Count});
    }
    [HttpPost("campaigns/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id,CancellationToken ct)
    {
        var tenant=User.TenantId();await using var tx=await db.Database.BeginTransactionAsync(ct);
        var c=await db.MarketingCampaigns.SingleOrDefaultAsync(x=>x.TenantId==tenant&&x.Id==id,ct);if(c==null)return NotFound();
        c.Status="Cancelled";
        var ids=db.CampaignRecipients.Where(r=>r.TenantId==tenant&&r.CampaignId==id).Select(r=>r.OutboxId);
        await Consent.CancelAsync(db,tenant,db.WhatsAppOutbox.Where(o=>ids.Contains(o.Id)),DateTime.UtcNow,"campaign_cancelled",ct);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return NoContent();
    }
    [HttpGet("campaigns")]
    public async Task<IActionResult> Campaigns(CancellationToken ct)
    {
        var tenant=User.TenantId();var rows=await db.MarketingCampaigns.AsNoTracking().Where(c=>c.TenantId==tenant).OrderByDescending(c=>c.CreatedAtUtc).Take(100).ToListAsync(ct);
        var ids=rows.Select(c=>c.Id).ToArray();
        var results=await (from r in db.CampaignRecipients.AsNoTracking() join o in db.WhatsAppOutbox.AsNoTracking() on r.OutboxId equals o.Id
                           where r.TenantId==tenant&&o.TenantId==tenant&&ids.Contains(r.CampaignId)
                           group r by new{r.CampaignId,o.Status} into g select new{g.Key.CampaignId,g.Key.Status,count=g.Count()}).ToListAsync(ct);
        var receipts=await (from r in db.CampaignRecipients.AsNoTracking() join o in db.WhatsAppOutbox.AsNoTracking() on r.OutboxId equals o.Id
            join m in db.WhatsAppMessages.AsNoTracking() on o.MessageId equals (Guid?)m.Id
            where r.TenantId==tenant&&o.TenantId==tenant&&m.TenantId==tenant&&ids.Contains(r.CampaignId)
            group r by new{r.CampaignId,m.Status} into g select new{g.Key.CampaignId,g.Key.Status,count=g.Count()}).ToListAsync(ct);
        return Ok(rows.Select(c=>new{c.Id,c.Name,c.Status,c.ScheduledAtUtc,counts=results.Where(r=>r.CampaignId==c.Id).ToDictionary(r=>r.Status,r=>r.count),
            messageStates=receipts.Where(r=>r.CampaignId==c.Id).ToDictionary(r=>r.Status,r=>r.count)}));
    }
}
