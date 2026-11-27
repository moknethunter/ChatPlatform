using System.Text.Json;
using ChatPlatform.Security;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Adapters;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Configuration;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Infrastructure.Engine;
using ChatPlatform.Infrastructure.Messaging;
using ChatPlatform.Hubs;
using ChatPlatform.Middleware;
using ChatPlatform.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Microsoft.AspNetCore.HttpOverrides;
var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
options.ForwardedHeaders =
ForwardedHeaders.XForwardedFor
| ForwardedHeaders.XForwardedProto;
// Trust loopback by default; accept additional proxies only when explicitly configured.
foreach (var address in builder.Configuration.GetSection("Security:TrustedProxies").Get<string[]>() ?? Array.Empty<string>())
{
    options.KnownProxies.Add(System.Net.IPAddress.Parse(address));
}
});
// ============================================================
// 1. WhatsApp Configuration
// ============================================================
var whatsAppSection = builder.Configuration.GetSection("WhatsApp");
var whatsAppToken =
Environment.GetEnvironmentVariable("WHATSAPP_TOKEN")
?? Environment.GetEnvironmentVariable("WhatsApp__AccessToken")
?? whatsAppSection["WHATSAPP_TOKEN"]
?? whatsAppSection["AccessToken"]
?? string.Empty;
var whatsAppPhoneId =
Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID")
?? Environment.GetEnvironmentVariable("WhatsApp__PhoneNumberId")
?? whatsAppSection["WHATSAPP_PHONE_NUMBER_ID"]
?? whatsAppSection["PhoneNumberId"]
?? string.Empty;
var whatsAppVerifyToken =
Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN")
?? Environment.GetEnvironmentVariable("WhatsApp__VerifyToken")
?? whatsAppSection["WHATSAPP_VERIFY_TOKEN"]
?? whatsAppSection["VerifyToken"]
?? string.Empty;
var whatsAppAppSecret =
Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET")
?? Environment.GetEnvironmentVariable("WhatsApp__AppSecret")
?? whatsAppSection["WHATSAPP_APP_SECRET"]
?? whatsAppSection["AppSecret"]
?? string.Empty;
var whatsAppApiVersion =
Environment.GetEnvironmentVariable("WHATSAPP_API_VERSION")
?? Environment.GetEnvironmentVariable("WHATSAPP_GRAPH_API_VERSION")
?? whatsAppSection["WHATSAPP_API_VERSION"]
?? whatsAppSection["GraphApiVersion"]
?? whatsAppSection["ApiVersion"]
?? "v26.0";
builder.Configuration["WhatsApp:AccessToken"] = whatsAppToken;
builder.Configuration["WhatsApp:PhoneNumberId"] = whatsAppPhoneId;
builder.Configuration["WhatsApp:VerifyToken"] = whatsAppVerifyToken;
builder.Configuration["WhatsApp:AppSecret"] = whatsAppAppSecret;
builder.Configuration["WhatsApp:ApiVersion"] = whatsAppApiVersion;
builder.Configuration["WhatsApp:GraphApiVersion"] = whatsAppApiVersion;
var whatsappOptions = builder.Configuration.GetSection(WhatsAppOptions.SectionName).Get<WhatsAppOptions>() ?? new WhatsAppOptions();
builder.Services.AddSingleton(whatsappOptions);
if (string.IsNullOrWhiteSpace(whatsAppToken) ||
string.IsNullOrWhiteSpace(whatsAppPhoneId) ||
string.IsNullOrWhiteSpace(whatsAppVerifyToken))
{
Console.WriteLine("⚠️ تحذير: إعدادات واتساب ناقصة. سيشتغل الخادم لكن واتساب لن يعمل حتى تكمل الإعدادات.");
}
else
{
Console.WriteLine($"✅ إعدادات واتساب موجودة - ApiVersion: {whatsAppApiVersion}");
}
if (string.IsNullOrWhiteSpace(whatsAppAppSecret))
{
Console.WriteLine("⚠️ تحذير: WhatsApp:AppSecret غير معين. راجع إعدادات التحقق من توقيع Meta قبل التشغيل.");
}
// ============================================================
// 2. Database - /data/platform.db
// ============================================================
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
?? "Data Source=/data/platform.db";
var sqliteBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
var dataDirectory = Environment.GetEnvironmentVariable("CHATPLATFORM_DATA_DIR") ?? "/data";
if (sqliteBuilder.DataSource.StartsWith("/app/", StringComparison.OrdinalIgnoreCase))
{
    sqliteBuilder.DataSource = Path.Combine(dataDirectory, Path.GetFileName(sqliteBuilder.DataSource));
}
if (sqliteBuilder.DataSource.Equals("/app", StringComparison.OrdinalIgnoreCase))
{
    sqliteBuilder.DataSource = Path.Combine(dataDirectory, "platform.db");
}
connectionString = sqliteBuilder.ToString();
Console.WriteLine($">>> SQLite database: {sqliteBuilder.DataSource}");
builder.Services.AddDbContext<PlatformDbContext>(options =>
options.UseSqlite(connectionString));
// ============================================================
// 3. Configuration and Data Protection - التعديل 1 هنا
// ============================================================
builder.Services.Configure<EngineOptions>(
builder.Configuration.GetSection("EngineSettings"));
builder.Services.AddDataProtection()
.PersistKeysToDbContext<PlatformDbContext>()
.SetApplicationName("ChatPlatform");
// ============================================================
// 4. Event Bus and Core Engine - التعديل 2 هنا
// ============================================================
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<CoreEngine>();
// ============================================================
// 5. Integration Adapters
// ============================================================
builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, ChatAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, UniversalConnectorAdapter>();
builder.Services.AddWhatsAppAdapter(builder.Configuration);
builder.Services.AddSingleton<IIntegrationAdapter, WhatsAppAutoReplyAdapter>();
// ============================================================
// 6. Controllers and HTTP Clients
// ============================================================
builder.Services.AddControllers();
builder.Services.AddSingleton<ChatPlatform.Infrastructure.Data.Connections.ExternalDatabaseRegistry>();
builder.Services.AddPlatformSecurity();
builder.Services.AddScoped<ChatPlatform.Infrastructure.AI.OwnModelClient>();
builder.Services.AddHttpClient("own-model",c=>c.Timeout=TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler {AllowAutoRedirect=false, UseCookies=false});
builder.Services.AddHttpClient("ResilientClient")
.AddPolicyHandler(
HttpPolicyExtensions.HandleTransientHttpError()
.WaitAndRetryAsync(
3,
retryAttempt =>
TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));
builder.Services.AddHttpClient("WhatsAppCloudApi", client =>
{
client.BaseAddress = new Uri($"https://graph.facebook.com/{whatsAppApiVersion}/");
client.DefaultRequestHeaders.Authorization =
new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", whatsAppToken);
client.Timeout = TimeSpan.FromSeconds(30);
});
// ============================================================
// 7. SignalR and CORS
// ============================================================
var allowedOrigins =
builder.Configuration
.GetSection("Cors:AllowedOrigins")
.Get<string[]>()
?? Array.Empty<string>();
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
options.AddDefaultPolicy(policy =>
{
policy
.SetIsOriginAllowed(origin =>
{
if (!Uri.TryCreate(
origin,
UriKind.Absolute,
out var uri))
return false;
return allowedOrigins.Contains(
uri.GetLeftPart(UriPartial.Authority),
StringComparer.OrdinalIgnoreCase);
})
.AllowAnyHeader()
.AllowAnyMethod()
.AllowCredentials();
});
});
// ============================================================
// 8. Build Application
// ============================================================
var app = builder.Build();
app.UseForwardedHeaders();
// ============================================================
// 9. Database Initialization
// ============================================================
using (var scope = app.Services.CreateScope())
{
try
{
DbInitializer.EnsureCreated(connectionString);
var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
await db.Database.CanConnectAsync();
var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
// Repair an empty token for the explicitly configured phone only, without overwriting
// existing credentials or activating/reassigning another organization's account.
var configuredPhone = whatsAppPhoneId.Trim();
if (!string.IsNullOrWhiteSpace(configuredPhone) && !string.IsNullOrWhiteSpace(whatsAppToken))
{
    var configuredTenant = Environment.GetEnvironmentVariable("WHATSAPP_TENANT_ID");
    if (string.IsNullOrWhiteSpace(configuredTenant))
        configuredTenant = "tenant-alhawbani-001";

    var account = await db.WhatsAppTenants.Include(a => a.Tenant)
        .SingleOrDefaultAsync(a => a.PhoneNumberId == configuredPhone && a.TenantId == configuredTenant
            && a.IsActive && a.Tenant != null && a.Tenant.IsActive);
    if (account != null && string.IsNullOrWhiteSpace(account.BusinessTokenEncrypted))
    {
        var protector = new WhatsAppTokenProtection(scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
        account.BusinessTokenEncrypted = protector.Protect(whatsAppToken);
        await db.SaveChangesAsync();
        logger.LogInformation("Initialized encrypted token for configured WhatsApp account.");
    }
}
logger.LogInformation("ChatPlatform database initialized successfully.");
}
catch (Exception ex)
{
var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
logger.LogError(ex, "Failed to initialize ChatPlatform database.");
throw;
}
}
// ============================================================
// 10. Middleware Pipeline - التعديل 3 و 4 هنا
// ============================================================
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseRouting();
app.UseCors();
// Never expose development-only seed operations in production.
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/dev/seed", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next(context);
    });
}
// تم حذف TenantResolutionMiddleware مؤقتاً - لا ننشئ وهمي
// آلية TenantResolver الحالية في WhatsApp تبقى كما هي
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.Use(PlatformSecurity.ProtectBrowserWrites);
app.UseRateLimiter();
app.MapPlatformSecurity();
// ============================================================
// 11. All Endpoints
// ============================================================
app.MapAllEndpoints(builder.Configuration);
// ============================================================
// 12. Run
// ============================================================
app.Run();
public partial class Program
{
}
