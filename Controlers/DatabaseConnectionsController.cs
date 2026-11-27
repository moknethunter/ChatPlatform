using ChatPlatform.Infrastructure.Data.Connections;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatPlatform.Controllers;

[ApiController, Route("api/database-connections"), Authorize(Policy = PlatformSecurity.AdminPolicy)]
public sealed class DatabaseConnectionsController(ExternalDatabaseRegistry registry) : ControllerBase
{
    [HttpGet]
    public IActionResult List()
    {
        Response.Headers["Cache-Control"] = "no-store";
        return Ok(registry.List(User.TenantId()));
    }

    [HttpPost("{id}/probe")]
    public async Task<IActionResult> Probe(string id,CancellationToken ct)
    {
        var tenant = User.TenantId();
        if (!registry.Contains(tenant,id)) return NotFound();
        Response.Headers["Cache-Control"] = "no-store";
        try
        {
            var value = await registry.ProbeAsync(tenant,id,ct);
            return value == "1" ? Ok(new { connected = true }) : StatusCode(503,new { error = "connection_unavailable" });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or ArgumentException or OperationCanceledException or IOException or TimeoutException)
        {
            // Provider errors can expose hosts, credentials or SQL. Never return them.
            return StatusCode(503,new { error = "connection_unavailable" });
        }
    }
}
