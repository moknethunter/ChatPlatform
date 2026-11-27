using ChatPlatform.Infrastructure.Adapters.Business;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatPlatform.Controllers;

// Operator-approved source pairs, tenant-admin access only.
[ApiController,Route("api/payment-reconciliation"),Authorize(Policy=PlatformSecurity.AdminPolicy),RequestSizeLimit(4096)]
public sealed class PaymentReconciliationController(PaymentReconciliation reconciliation,ILogger<PaymentReconciliationController> log):ControllerBase
{
    public sealed record CompareRequest(string? InvoiceReference,string? PaymentReference);

    [HttpGet]
    public IActionResult List()
    {
        Response.Headers["Cache-Control"]="no-store";
        return Ok(reconciliation.List(User.TenantId()));
    }

    [HttpPost("{id}/compare")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("business-read")]
    public async Task<IActionResult> Compare(string id,CompareRequest request,CancellationToken ct)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(!BusinessReadRegistry.ValidReference(request.InvoiceReference)||!BusinessReadRegistry.ValidReference(request.PaymentReference))
            return BadRequest(new{error="invalid_reference"});
        var tenant=User.TenantId();
        try
        {
            var result=await reconciliation.CompareAsync(tenant,id,request.InvoiceReference!,request.PaymentReference!,ct);
            // Log outcome, not customer references or financial data.
            log.LogInformation("Payment comparison tenant={Tenant} outcome={Outcome}",tenant,result.Outcome);
            return Ok(result);
        }
        catch(KeyNotFoundException){return NotFound(new{error="pair_unavailable"});}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex) when(ex is System.Data.Common.DbException or InvalidOperationException or ArgumentException
            or OperationCanceledException or IOException or TimeoutException or HttpRequestException or System.Text.Json.JsonException)
        {
            log.LogWarning("Payment comparison failed tenant={Tenant}",tenant);
            return StatusCode(503,new{error="integration_unavailable"});
        }
    }
}
