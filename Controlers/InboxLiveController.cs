using System.Globalization;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Controllers;

[ApiController, Route("api/live/inbox")]
[Authorize(Policy = PlatformSecurity.ReadPolicy)]
[EnableRateLimiting("inbox-live")]
public sealed class InboxLiveController(PlatformDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task Stream(CancellationToken ct)
    {
        // No caller-selected tenant, message data or cross-site streaming.
        var origin = Request.Headers["Origin"].ToString();
        var site = Request.Headers["Sec-Fetch-Site"].ToString();
        if (site is "cross-site" or "same-site" ||
            (origin.Length > 0 && !string.Equals(origin, $"{Request.Scheme}://{Request.Host}", StringComparison.OrdinalIgnoreCase)))
        {
            Response.StatusCode = 403;
            return;
        }
        var tenant = User.TenantId();
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-store, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";
        long? last = null;
        try
        {
            await Response.WriteAsync("retry: 3000\n\n", ct);
            for (int i = 0; i < 25; i++)
            {
                if (!await PlatformSecurity.IsSessionValidAsync(configuration, db, User, ct))
                {
                    await Response.WriteAsync("event: session-expired\ndata: 1\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    return;
                }
                var revision = await db.InboxRevisions.AsNoTracking().Where(x => x.TenantId == tenant)
                    .Select(x => (long?)x.Revision).SingleOrDefaultAsync(ct) ?? 0;
                if (revision != last)
                {
                    await Response.WriteAsync("event: inbox\ndata: " + revision.ToString(CultureInfo.InvariantCulture) + "\n\n", ct);
                    last = revision;
                }
                else if (i % 5 == 0) await Response.WriteAsync(": keepalive\n\n", ct);
                await Response.Body.FlushAsync(ct);
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
