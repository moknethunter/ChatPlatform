using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Security;

// Operator-provisioned administrators and database-managed agents. No credentials are committed.
public sealed class PlatformAccount
{
    public string Username { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Role { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}
public static class PlatformSecurity
{
    public const string ReadPolicy = "TenantRead";
    public const string AdminPolicy = "TenantAdmin";
    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private const string Stamp = "security_stamp";
    private const string Session = "platform_session";
    private static readonly PasswordHasher<PlatformAccount> Hasher = new();
    private static readonly PlatformAccount Dummy = new();
    private static readonly string DummyHash = Hasher.HashPassword(Dummy, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    internal static bool IsReservedUsername(IConfiguration cfg, string username) =>
        (cfg.GetSection("Security:Accounts").Get<PlatformAccount[]>() ?? Array.Empty<PlatformAccount>())
            .Any(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));

    internal static string HashAgentPassword(string password) => Hasher.HashPassword(Dummy, password);

    private static async Task<PlatformAccount?> FindAsync(IConfiguration cfg, PlatformDbContext db, string? username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var matches = (cfg.GetSection("Security:Accounts").Get<PlatformAccount[]>() ?? Array.Empty<PlatformAccount>())
            .Where(a => string.Equals(a.Username, username, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length == 0)
        {
            // Configuration identities remain reserved even if their spelling differs.
            if (IsReservedUsername(cfg, username)) return null;
            var agent = await db.ManagedAgents.AsNoTracking().SingleOrDefaultAsync(x => x.Username == username && x.IsActive, ct);
            if (agent == null) return null;
            return new PlatformAccount { Username = agent.Username, TenantId = agent.TenantId,
                Role = "Agent", PasswordHash = agent.PasswordHash };
        }
        if (matches.Length != 1) return null;
        var a = matches[0];
        return !string.IsNullOrWhiteSpace(a.TenantId) && !string.IsNullOrWhiteSpace(a.PasswordHash)
            && a.Role is "Admin" or "Agent" ? a : null;
    }

    private static string Fingerprint(PlatformAccount account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.PasswordHash)));

    public static string TenantId(this ClaimsPrincipal user) =>
        user.FindFirstValue("tenant_id") ?? throw new InvalidOperationException("Tenant identity is required.");

    internal static async Task<bool> IsSessionValidAsync(IConfiguration cfg, PlatformDbContext db, ClaimsPrincipal? principal, CancellationToken ct)
    {
        var account = await FindAsync(cfg, db, principal?.Identity?.Name, ct);
        var sessionId = principal?.FindFirstValue(Session);
        var now = DateTime.UtcNow;
        return account != null && sessionId != null &&
            principal?.FindFirstValue("tenant_id") == account.TenantId &&
            principal?.FindFirstValue(ClaimTypes.Role) == account.Role &&
            principal?.FindFirstValue(Stamp) == Fingerprint(account) &&
            await db.Tenants.AnyAsync(t => t.Id == account.TenantId && t.IsActive, ct) &&
            await db.PlatformSessions.AnyAsync(s => s.Id == sessionId && s.Username == account.Username
                && s.TenantId == account.TenantId && s.ExpiresAtUtc > now, ct);
    }

    public static IServiceCollection AddPlatformSecurity(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme).AddCookie(options =>
        {
            options.Cookie.Name = "__Host-ChatPlatform";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = async c =>
            {
                var cfg = c.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                var db = c.HttpContext.RequestServices.GetRequiredService<PlatformDbContext>();
                if (!await IsSessionValidAsync(cfg, db, c.Principal, c.HttpContext.RequestAborted))
                {
                    c.RejectPrincipal();
                    await c.HttpContext.SignOutAsync(Scheme);
                }
            };
        });
        services.AddAuthorization(options =>
        {
            // Every new endpoint must explicitly choose a reviewed policy or public access.
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build();
            options.AddPolicy(ReadPolicy, p => p.RequireAuthenticatedUser().RequireClaim("tenant_id").RequireRole("Admin", "Agent"));
            options.AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireClaim("tenant_id").RequireRole("Admin"));
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("media", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.Identity?.Name ?? "anonymous", _ => new FixedWindowRateLimiterOptions {
                    PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("inbox-live", context => RateLimitPartition.GetConcurrencyLimiter(
                context.User.Identity?.Name ?? "anonymous", _ => new ConcurrencyLimiterOptions { PermitLimit = 2, QueueLimit = 0 }));
            options.AddFixedWindowLimiter("oauth-policy", o => { o.PermitLimit = 10; o.Window = TimeSpan.FromMinutes(1); o.QueueLimit = 0; });
        });
        return services;
    }

    // A custom header prevents ordinary cross-origin forms; reject cross-site browser fetches too.
    public static async Task ProtectBrowserWrites(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method))
        {
            var site = request.Headers["Sec-Fetch-Site"].ToString();
            var origin = request.Headers["Origin"].ToString();
            var expectedOrigin = $"{request.Scheme}://{request.Host}";
            if (request.Headers["X-ChatPlatform-CSRF"].ToString() != "1"
                || site == "cross-site" || site == "same-site"
                || (!string.IsNullOrEmpty(origin) && !string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = 403;
                return;
            }
        }
        await next(context);
    }

    public sealed record LoginRequest(string Username, string Password);

    public static void MapPlatformSecurity(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest req, HttpContext ctx, IConfiguration cfg, PlatformDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            var logger = logs.CreateLogger("Authentication");
            if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length > 100
                || string.IsNullOrEmpty(req.Password) || req.Password.Length > 1024)
                return Results.Unauthorized();
            var a = await FindAsync(cfg, db, req.Username, ct);
            PasswordVerificationResult result;
            try { result = Hasher.VerifyHashedPassword(a ?? Dummy, a?.PasswordHash ?? DummyHash, req.Password); }
            catch (FormatException) { result = PasswordVerificationResult.Failed; }
            if (a == null || result == PasswordVerificationResult.Failed
                || !await db.Tenants.AnyAsync(t => t.Id == a.TenantId && t.IsActive, ct))
            {
                logger.LogWarning("Rejected login attempt.");
                return Results.Unauthorized();
            }
            var now = DateTime.UtcNow;
            var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            // Expired sessions are not valid, even if their old cookies are replayed.
            await db.PlatformSessions.Where(s => s.ExpiresAtUtc <= now).ExecuteDeleteAsync(ct);
            db.PlatformSessions.Add(new PlatformSessionEntity
            {
                Id = sessionId, Username = a.Username, TenantId = a.TenantId, ExpiresAtUtc = now.AddMinutes(30)
            });
            await db.SaveChangesAsync(ct);
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, a.Username),
                new Claim(ClaimTypes.Name, a.Username),
                new Claim(ClaimTypes.Role, a.Role),
                new Claim("tenant_id", a.TenantId),
                new Claim(Stamp, Fingerprint(a)),
                new Claim(Session, sessionId)
            }, Scheme);
            await ctx.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, ExpiresUtc = new DateTimeOffset(now.AddMinutes(30)) });
            logger.LogInformation("Login succeeded for {User} in tenant {Tenant}.", a.Username, a.TenantId);
            return Results.Ok(new { username = a.Username, tenantId = a.TenantId, role = a.Role });
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new
        {
            username = user.Identity!.Name, tenantId = user.TenantId(), role = user.FindFirstValue(ClaimTypes.Role)
        })).RequireAuthorization(ReadPolicy);

        app.MapPost("/api/auth/logout", async (HttpContext ctx, PlatformDbContext db, CancellationToken ct) =>
        {
            var sessionId = ctx.User.FindFirstValue(Session);
            await db.PlatformSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);
            await ctx.SignOutAsync(Scheme);
            return Results.NoContent();
        }).RequireAuthorization(ReadPolicy);
    }
}
