// Use the external registry, not primary DB / سجل المصادر الخارجية.
using ChatPlatform.Infrastructure.Data.Connections;
// Derive tenant identity from authentication / هوية المؤسسة.
using ChatPlatform.Security;
// Require explicit role policy / سياسة صلاحيات.
using Microsoft.AspNetCore.Authorization;
// HTTP controller and response types / واجهات HTTP.
using Microsoft.AspNetCore.Mvc;

namespace ChatPlatform.Controllers;

// Tenant-admin-only route / مسار للمدير.
[ApiController, Route("api/database-connections"), Authorize(Policy = PlatformSecurity.AdminPolicy)]
// Inject registry without accessing credentials directly / حقن السجل.
public sealed class DatabaseConnectionsController(ExternalDatabaseRegistry registry) : ControllerBase
{
    // List aliases without exposing connection strings / عرض المعرفات.
    [HttpGet]
    // Metadata for the authenticated tenant / معلومات المؤسسة.
    public IActionResult List()
    {
        // Do not cache tenant-specific responses / منع التخزين المؤقت.
        Response.Headers["Cache-Control"] = "no-store";
        // Identity is server-derived, never body supplied / نطاق موثوق.
        return Ok(registry.List(User.TenantId()));
    }

    // Connectivity probe, not arbitrary SQL / اختبار اتصال.
    [HttpPost("{id}/probe")]
    // Bounded asynchronous probe with request cancellation / فحص محدود.
    public async Task<IActionResult> Probe(string id,CancellationToken ct)
    {
        // Use authenticated tenant only / مؤسسة مصادق عليها.
        var tenant = User.TenantId();
        // Hide existence of another tenant's source / رفض المصدر الأجنبي.
        if (!registry.Contains(tenant,id)) return NotFound();
        // Do not cache tenant-specific responses / منع التخزين المؤقت.
        Response.Headers["Cache-Control"] = "no-store";
        // Map known operational failures to sanitized status / معالجة الأعطال.
        try
        {
            // Run fixed SELECT 1 through the chosen adapter / استعلام ثابت.
            var value = await registry.ProbeAsync(tenant,id,ct);
            // Return only connectivity status / نتيجة الاتصال فقط.
            return value == "1" ? Ok(new { connected = true }) : StatusCode(503,new { error = "connection_unavailable" });
        }
        // Respect caller cancellation / احترام الإلغاء.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Catch expected provider or configuration failures / أخطاء متوقعة.
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or ArgumentException or OperationCanceledException or IOException or TimeoutException)
        {
            // Provider errors can expose hosts, credentials or SQL. Never return them.
            // No raw provider exceptions or secrets / لا كشف تفاصيل.
            return StatusCode(503,new { error = "connection_unavailable" });
        }
    }
}
