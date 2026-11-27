using ChatPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ChatPlatform.Infrastructure.Adapters.Business;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatPlatform.Controllers;

// Read-only administrative integration access, not a public payment API.
// واجهة للمدير فقط، لا تنفذ التحصيل أو التحويل.
[ApiController, Route("api/business-integrations"), Authorize(Policy=PlatformSecurity.AdminPolicy), RequestSizeLimit(4096)]
public sealed class BusinessIntegrationsController(BusinessReadRegistry registry, ILogger<BusinessIntegrationsController> log) : ControllerBase
{
    public sealed record ReadRequest(string? Operation,string? Reference);

    [HttpGet]
    public IActionResult List()
    {
        Response.Headers["Cache-Control"]="no-store";
        return Ok(registry.List(User.TenantId()));
    }

    // Only tenant admins can see pending bot requests; no financial result is stored here.
    [HttpGet("requests")]
    public async Task<IActionResult> Requests([FromServices] PlatformDbContext db,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        var tenant=User.TenantId();
        var rows=await (from s in db.ConversationAutomation.AsNoTracking()
            join conversation in db.WhatsAppConversations.AsNoTracking() on s.ConversationId equals conversation.Id
            where s.TenantId==tenant && conversation.TenantId==tenant && s.Mode=="human" && s.BusinessSource!=null
            orderby s.UpdatedAtUtc descending,s.ConversationId
            select new {s.ConversationId,s.BusinessSource,s.BusinessOperation,s.UpdatedAtUtc,conversation.ContactPhone}).Take(100).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("{id}/read")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("business-read")]
    public async Task<IActionResult> Read(string id,ReadRequest request,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        if (!BusinessReadRegistry.TryKind(request.Operation,out var kind) ||
            !BusinessReadRegistry.ValidReference(request.Reference)) return BadRequest(new { error="invalid_read" });
        var tenant=User.TenantId();
        try
        {
            var result=await registry.ReadAsync(tenant,id,kind,request.Reference!,ct);
            // Audit attempt metadata only; no financial result or reference in logs.
            log.LogInformation("Business read tenant={Tenant} operation={Operation} found={Found}",tenant,kind,result!=null);
            return result==null ? NotFound(new { error="record_unavailable" }) : Ok(result);
        }
        catch (KeyNotFoundException) { return NotFound(new { error="record_unavailable" }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or ArgumentException
            or OperationCanceledException or IOException or TimeoutException or HttpRequestException or System.Text.Json.JsonException)
        {
            // Never return/log raw external errors that might contain financial data or credentials.
            log.LogWarning("Business read failed tenant={Tenant} operation={Operation}",tenant,kind);
            return StatusCode(503,new { error="integration_unavailable" });
        }
    }
}
