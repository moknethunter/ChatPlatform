using System.Text.Json;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Infrastructure.Preview;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;
[ApiController,Route("api/preview"),Authorize(Policy=PlatformSecurity.ReadPolicy),RequestSizeLimit(8192)]
public sealed class PreviewController(PlatformDbContext db,IWebHostEnvironment env):ControllerBase
{
    [HttpGet]
    public IActionResult Status()=>Ok(new{enabled=env.IsEnvironment("Preview")&&User.TenantId()==PreviewSetup.Tenant});
    public record InboundRequest(Guid ConversationId,string? Text);
    [HttpPost("inbound")]
    public async Task<IActionResult> Inbound(InboundRequest r,CancellationToken ct)
    {
        if(!env.IsEnvironment("Preview")||User.TenantId()!=PreviewSetup.Tenant)return NotFound();
        if(string.IsNullOrWhiteSpace(r.Text)||r.Text.Length>1000)return BadRequest();
        var c=await db.WhatsAppConversations.AsNoTracking().SingleOrDefaultAsync(c=>c.TenantId==PreviewSetup.Tenant&&c.Id==r.ConversationId&&c.PhoneNumberId==PreviewSetup.PhoneId,ct);
        if(c==null)return NotFound();
        var id="preview."+Guid.NewGuid().ToString("N");var now=DateTime.UtcNow;
        db.WhatsAppWebhookInbox.Add(new WhatsAppWebhookInboxEntity{TenantId=c.TenantId,PhoneNumberId=c.PhoneNumberId,EventKey=id,
            PayloadJson=JsonSerializer.Serialize(new{id,from=c.ContactPhone,type="text",timestamp=new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),text=new{body=r.Text}}),ReceivedAtUtc=now,NextAttemptAtUtc=now});
        await db.SaveChangesAsync(ct);return Accepted();
    }
}
