using System.Security.Cryptography;
using System.Text;
using ChatPlatform.Infrastructure.Adapters.Business;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

// Staff-assisted verification, not customer authentication or an automatic WhatsApp disclosure.
// تحقق بشري مستقل قبل الاستعلام المالي؛ لا يرسل أي نتيجة للعميل تلقائيًا.
[ApiController,Route("api/business-verification"),Authorize(Policy=PlatformSecurity.AdminPolicy),RequestSizeLimit(4096)]
public sealed class BusinessVerificationController(PlatformDbContext db,BusinessReadRegistry registry,
    IDataProtectionProvider protection,ILogger<BusinessVerificationController> log):ControllerBase
{
    public sealed record VerifyRequest(string? Reference,bool IndependentlyVerified,string? Source,string? Operation,DateTime? RequestUpdatedAtUtc);
    public sealed record ConsumeRequest(Guid GrantId);

    // Scope both the conversation and the active receiving account to the authenticated tenant.
    private IQueryable<ConversationAutomationEntity> Pending(string tenant,Guid id) =>
        db.ConversationAutomation.Where(s=>s.TenantId==tenant && s.ConversationId==id &&
            s.Mode=="human" && s.BusinessSource!=null && s.BusinessOperation!=null &&
            db.WhatsAppConversations.Any(c=>c.Id==id && c.TenantId==tenant &&
                db.WhatsAppTenants.Any(a=>a.TenantId==tenant && a.PhoneNumberId==c.PhoneNumberId && a.IsActive)));

    // Bind grants to the authenticated session without persisting its bearer identifier.
    private string? SessionHash() => User.FindFirst("platform_session")?.Value is string session
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session))) : null;

    private IDataProtector Protector(string tenant,Guid conversation,Guid grant) =>
        protection.CreateProtector("ChatPlatform.BusinessVerification.v1",tenant,conversation.ToString("N"),grant.ToString("N"));

    [HttpPost("{conversationId:guid}/grant")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("business-read")]
    public async Task<IActionResult> Grant(Guid conversationId,VerifyRequest request,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        // An explicit attestation is mandatory; a WhatsApp number alone is not verification.
        if(!request.IndependentlyVerified || !BusinessReadRegistry.ValidReference(request.Reference))
            return BadRequest(new{error="independent_verification_required"});
        var tenant=User.TenantId();
        var actor=User.Identity!.Name!;
        var session=SessionHash();if(session==null)return Forbid();
        var now=DateTime.UtcNow;
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var pending=await Pending(tenant,conversationId).AsNoTracking().SingleOrDefaultAsync(ct);
        if(pending==null || !registry.Allows(tenant,pending.BusinessSource!,pending.BusinessOperation!))
            return NotFound(new{error="request_unavailable"});
        if(request.Source!=pending.BusinessSource || request.Operation!=pending.BusinessOperation || request.RequestUpdatedAtUtc?.Ticks!=pending.UpdatedAtUtc.Ticks)
            return Conflict(new{error="request_changed"});
        // Never store the plaintext account/invoice/payment reference. Replace the previous grant.
        var grantId=Guid.NewGuid();
        var ciphertext=Protector(tenant,conversationId,grantId).Protect(request.Reference!);
        var expiry=now.AddMinutes(5);
        await db.BusinessVerificationGrants.Where(x=>x.TenantId==tenant && x.ConversationId==conversationId).ExecuteDeleteAsync(ct);
        db.BusinessVerificationGrants.Add(new(){TenantId=tenant,ConversationId=conversationId,GrantId=grantId,
            AdminUsername=actor,SessionHash=session,Source=pending.BusinessSource!,Operation=pending.BusinessOperation!,
            ProtectedReference=ciphertext,RequestUpdatedAtUtc=pending.UpdatedAtUtc,ExpiresAtUtc=expiry});
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        log.LogInformation("Business verification attested tenant={Tenant} admin={Admin} operation={Operation}",tenant,actor,pending.BusinessOperation);
        return Ok(new{grantId,expiresAtUtc=expiry});
    }

    [HttpPost("{conversationId:guid}/read")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("business-read")]
    public async Task<IActionResult> Read(Guid conversationId,ConsumeRequest request,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        var tenant=User.TenantId();
        var actor=User.Identity!.Name!;
        var session=SessionHash();if(session==null)return Forbid();
        var now=DateTime.UtcNow;
        var grants=db.BusinessVerificationGrants.Where(x=>x.TenantId==tenant && x.ConversationId==conversationId &&
            x.GrantId==request.GrantId && x.AdminUsername==actor && x.SessionHash==session && x.ExpiresAtUtc>now);
        var grant=await grants.AsNoTracking().SingleOrDefaultAsync(ct);
        if(grant==null)return NotFound(new{error="verification_unavailable"});
        var pending=Pending(tenant,conversationId).Where(s=>s.UpdatedAtUtc==grant.RequestUpdatedAtUtc &&
            s.BusinessSource==grant.Source && s.BusinessOperation==grant.Operation);
        // Conditional delete atomically consumes the grant, even if the remote service later fails.
        // No external network I/O inside a primary-database transaction.
        if(await grants.Where(x=>pending.Any()).ExecuteDeleteAsync(ct)!=1)
            return NotFound(new{error="verification_unavailable"});
        try
        {
            if(!BusinessReadRegistry.TryKind(grant.Operation,out var kind))return NotFound();
            var reference=Protector(tenant,conversationId,grant.GrantId).Unprotect(grant.ProtectedReference);
            var result=await registry.ReadAsync(tenant,grant.Source,kind,reference,ct);
            // If the conversation was returned to bot mode, do not expose a late result.
            if(!await pending.AnyAsync(ct))return NotFound(new{error="verification_unavailable"});
            log.LogInformation("Verified business lookup tenant={Tenant} admin={Admin} operation={Operation}",tenant,actor,grant.Operation);
            return result==null?NotFound(new{error="record_unavailable"}):Ok(result);
        }
        catch(KeyNotFoundException){return NotFound(new{error="verification_unavailable"});}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex) when(ex is CryptographicException or System.Data.Common.DbException or InvalidOperationException or ArgumentException
            or OperationCanceledException or IOException or TimeoutException or HttpRequestException or System.Text.Json.JsonException)
        {
            log.LogWarning("Verified business lookup failed tenant={Tenant} operation={Operation}",tenant,grant.Operation);
            return StatusCode(503,new{error="integration_unavailable"});
        }
    }

    [HttpDelete("{conversationId:guid}/grant")]
    public async Task<IActionResult> Revoke(Guid conversationId,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        var tenant=User.TenantId();
        var removed=await db.BusinessVerificationGrants.Where(x=>x.TenantId==tenant && x.ConversationId==conversationId).ExecuteDeleteAsync(ct);
        return removed==0?NotFound():NoContent();
    }
}
