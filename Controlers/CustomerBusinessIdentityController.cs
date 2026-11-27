using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Infrastructure.Adapters.Business;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace ChatPlatform.Controllers;

// Administrative attestation only. WhatsApp phone possession alone is never identity proof.
// يعتمد على تحقق مستقل يقوم به المدير، ولا يرسل نتائج مالية للعميل.
[ApiController,Route("api/customer-business-identity"),Authorize(Policy=PlatformSecurity.AdminPolicy),RequestSizeLimit(4096)]
public sealed class CustomerBusinessIdentityController(PlatformDbContext db,BusinessReadRegistry registry,
    IDataProtectionProvider protection,ILogger<CustomerBusinessIdentityController> log):ControllerBase
{
    public sealed record BindRequest(string? Source,string? Operation,string? Reference,string? VerificationMethod,bool Confirmed);
    public sealed record BoundReadRequest(string? Source,string? Operation);
    private string Tenant=>User.TenantId();
    private IDataProtector Protector(Guid id,string source,string operation)=>protection.CreateProtector(
        "ChatPlatform.CustomerBusinessBinding.v1",Tenant,id.ToString("D"),source,operation);
    private IQueryable<CustomerBusinessBindingEntity> Rows(Guid id)=>db.CustomerBusinessBindings
        .Where(x=>x.TenantId==Tenant&&x.ConversationId==id);
    private Task<bool> OwnsActiveConversation(Guid id,CancellationToken ct)=>db.WhatsAppConversations.AsNoTracking()
        .AnyAsync(c=>c.TenantId==Tenant&&c.Id==id&&db.WhatsAppTenants.Any(a=>a.TenantId==Tenant&&a.PhoneNumberId==c.PhoneNumberId&&a.IsActive),ct);
    private static bool ValidScope(string? source,string? operation)=>BusinessReadRegistry.ValidSourceAlias(source)&&BusinessReadRegistry.TryKind(operation,out _);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> List(Guid id,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(!await OwnsActiveConversation(id,ct))return NotFound();
        var now=DateTime.UtcNow;
        return Ok(await Rows(id).AsNoTracking().OrderByDescending(x=>x.VerifiedAtUtc).Take(100)
            .Select(x=>new{x.Source,x.Operation,x.VerifiedBy,x.VerificationMethod,x.VerifiedAtUtc,x.ExpiresAtUtc,
                active=!x.Revoked&&x.ExpiresAtUtc>now}).ToListAsync(ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Bind(Guid id,BindRequest request,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(!request.Confirmed||request.VerificationMethod is not ("in-person" or "authenticated-customer-portal")||
            !ValidScope(request.Source,request.Operation)||!BusinessReadRegistry.ValidReference(request.Reference))
            return BadRequest(new{error="independent_verification_required"});
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(!await OwnsActiveConversation(id,ct)||!registry.Allows(Tenant,request.Source!,request.Operation!))return NotFound();
        var row=await Rows(id).SingleOrDefaultAsync(x=>x.Source==request.Source&&x.Operation==request.Operation,ct);
        if(row==null)
        {
            if(await Rows(id).CountAsync(ct)>=100)return Conflict(new{error="binding_limit"});
            row=new CustomerBusinessBindingEntity{TenantId=Tenant,ConversationId=id,Source=request.Source!,Operation=request.Operation!};
            db.CustomerBusinessBindings.Add(row);
        }
        var now=DateTime.UtcNow;
        row.ProtectedReference=Protector(id,row.Source,row.Operation).Protect(request.Reference!);
        row.VerifiedBy=User.Identity!.Name!;
        row.VerificationMethod=request.VerificationMethod!;
        row.VerifiedAtUtc=now;row.ExpiresAtUtc=now.AddHours(24);row.Revoked=false;row.Revision=Guid.NewGuid();
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        log.LogInformation("Customer binding attested tenant={Tenant} conversation={Conversation} source={Source} operation={Operation} actor={Actor}",Tenant,id,row.Source,row.Operation,row.VerifiedBy);
        return Ok(new{row.Source,row.Operation,row.ExpiresAtUtc});
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id,BoundReadRequest request,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(!ValidScope(request.Source,request.Operation))return BadRequest();
        // Revocation is permitted even after the associated channel is disabled.
        var affected=await Rows(id).Where(x=>x.Source==request.Source&&x.Operation==request.Operation&&!x.Revoked)
            .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Revoked,true).SetProperty(x=>x.ProtectedReference,"")
                .SetProperty(x=>x.Revision,Guid.NewGuid()),ct);
        if(affected==0)return NotFound();
        log.LogInformation("Customer binding revoked tenant={Tenant} conversation={Conversation} actor={Actor}",Tenant,id,User.Identity!.Name);
        return NoContent();
    }

    [HttpPost("{id:guid}/read")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("business-read")]
    public async Task<IActionResult> Read(Guid id,BoundReadRequest request,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(!ValidScope(request.Source,request.Operation)||!BusinessReadRegistry.TryKind(request.Operation,out var kind))return BadRequest();
        if(!await OwnsActiveConversation(id,ct))return NotFound();
        var now=DateTime.UtcNow;
        var binding=await Rows(id).AsNoTracking().SingleOrDefaultAsync(x=>x.Source==request.Source&&x.Operation==request.Operation&&!x.Revoked&&x.ExpiresAtUtc>now,ct);
        if(binding==null)return NotFound(new{error="binding_unavailable"});
        try
        {
            // The stored reference, not customer input, is used. No DB write transaction spans I/O.
            var reference=Protector(id,binding.Source,binding.Operation).Unprotect(binding.ProtectedReference);
            var result=await registry.ReadAsync(Tenant,binding.Source,kind,reference,ct);
            now=DateTime.UtcNow;
            // Discard response if the attestation was replaced, revoked or expired during I/O.
            if(!await OwnsActiveConversation(id,ct)||!await Rows(id).AnyAsync(x=>x.Source==binding.Source&&x.Operation==binding.Operation&&x.Revision==binding.Revision&&!x.Revoked&&x.ExpiresAtUtc>now,ct))return Conflict(new{error="binding_changed"});
            log.LogInformation("Bound business lookup tenant={Tenant} conversation={Conversation} operation={Operation} actor={Actor}",Tenant,id,binding.Operation,User.Identity!.Name);
            return result==null?NotFound(new{error="record_unavailable"}):Ok(result);
        }
        catch(KeyNotFoundException){return NotFound(new{error="record_unavailable"});}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex) when(ex is CryptographicException or System.Data.Common.DbException or InvalidOperationException or ArgumentException or OperationCanceledException or IOException or TimeoutException or HttpRequestException or System.Text.Json.JsonException)
        {
            return StatusCode(503,new{error="integration_unavailable"});
        }
    }
}
