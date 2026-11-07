using System.Text.Json;
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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════════════════════════
// 1) قاعدة البيانات
// ══════════════════════════════════════════════════════════════
builder.Services.AddDbContext<PlatformDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=chatplatform.db"));

// ══════════════════════════════════════════════════════════════
// 2) الإعدادات وحماية البيانات
// ══════════════════════════════════════════════════════════════
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("EngineSettings"));
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<PlatformDbContext>()
    .SetApplicationName("ChatPlatform");

// ══════════════════════════════════════════════════════════════
// 3) ناقل الأحداث والمحرك
// ══════════════════════════════════════════════════════════════
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<CoreEngine>();

// ══════════════════════════════════════════════════════════════
// 4) المحوّلات (Adapters)
// ══════════════════════════════════════════════════════════════
builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, ChatAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, UniversalConnectorAdapter>();
builder.Services.AddWhatsAppAdapter(builder.Configuration);
// SignalRAdapter معطّل مؤقتاً (لم نعد نحتاجه، الواجهة تستخدم Polling)

// ══════════════════════════════════════════════════════════════
// 5) Controllers + HttpClient
// ══════════════════════════════════════════════════════════════
builder.Services.AddControllers();
builder.Services.AddHttpClient("ResilientClient")
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

// ══════════════════════════════════════════════════════════════
// 6) SignalR + CORS
// ══════════════════════════════════════════════════════════════
builder.Services.AddSignalR();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin =>
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        return configuredOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);
    })
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

// ══════════════════════════════════════════════════════════════
// 7) تهيئة قاعدة البيانات — 🔴 التعديل الحاسم
// ══════════════════════════════════════════════════════════════
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    try
    {
        // ✅ يُنشئ كل الجداول من النماذج تلقائياً
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to initialize database.");
        throw;
    }
}

// ══════════════════════════════════════════════════════════════
// 8) Middlewares — ⚠️ ترتيب CORS أولاً ثم ApiKey
// ══════════════════════════════════════════════════════════════
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseCors();                                       // ← أولاً
app.UseMiddleware<ApiKeyMiddleware>();               // ← ثانياً
app.UseDefaultFiles();
app.UseStaticFiles();

// ══════════════════════════════════════════════════════════════
// 9) نقاط النهاية
// ══════════════════════════════════════════════════════════════
app.MapGet("/api/platform-info", (HttpRequest request) =>
{
    var baseUrl = $"{request.Scheme}://{request.Host}";
    return Results.Ok(new
    {
        status = "Online",
        version = "1.0.0",
        endpoints = new
        {
            whatsappWebhook = $"{baseUrl}/webhook",
            events = $"{baseUrl}/api/events",
            connectors = $"{baseUrl}/api/connectors",
            whatsappOnboarding = $"{baseUrl}/api/whatsapp/exchange-code",
            health = $"{baseUrl}/health"
        },
        legal = new
        {
            privacy = $"{baseUrl}/privacy.html",
            terms = $"{baseUrl}/terms.html",
            dataDeletion = $"{baseUrl}/data-deletion.html"
        }
    });
});

var connectorsGroup = app.MapGroup("/api/connectors");
connectorsGroup.MapGet("/", async (PlatformDbContext db) =>
    Results.Ok(await db.Connectors.AsNoTracking().ToListAsync()));
connectorsGroup.MapPost("/", async (ConnectorEntity req, PlatformDbContext db) =>
{
    req.Id = Guid.NewGuid().ToString();
    db.Connectors.Add(req);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "تم الحفظ بنجاح", id = req.Id });
});

app.MapPost("/api/inbound/{platform}", async (string platform, HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    JsonElement json;
    try { json = JsonDocument.Parse(body).RootElement; }
    catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON body" }); }
    await bus.PublishAsync(new PlatformEvent { Type = "message.created", Source = platform, Payload = json });
    return Results.Ok(new { status = "received" });
});

app.MapPost("/api/events", async (HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    JsonElement json;
    try { json = JsonDocument.Parse(body).RootElement; }
    catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON body" }); }
    var eventType = json.TryGetProperty("type", out var type) ? type.GetString() : "unknown.event";
    await bus.PublishAsync(new PlatformEvent
    {
        Type = eventType ?? "unknown.event",
        Source = "http-api",
        Payload = json
    });
    return Results.Accepted();
});

app.MapGet("/health", () => Results.Ok(new { status = "Core is running", timestamp = DateTimeOffset.UtcNow }));
app.MapHub<ChatHub>("/hub/chat");
app.MapWhatsAppWebhook(builder.Configuration);
app.MapControllers();
app.Run();

public partial class Program { }
