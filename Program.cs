using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Hubs;
using ChatPlatform.Infrastructure.Adapters;
using ChatPlatform.Infrastructure.Configuration;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Engine;
using ChatPlatform.Infrastructure.Messaging;
using ChatPlatform.Middleware;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

// ═══════════════════════════════════════════════════════════════════════════
// 1. قاعدة البيانات (يجب أن تكون الأولى — كل شيء يعتمد عليها)
// ═══════════════════════════════════════════════════════════════════════════
builder.Services.AddDbContext<PlatformDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("Default")
        ?? "Data Source=chatplatform.db"));

builder.Services.Configure<EngineOptions>(
    builder.Configuration.GetSection("EngineSettings"));

// ═══════════════════════════════════════════════════════════════════════════
// 2. حماية البيانات (تشفير توكنات العملاء — مفاتيح في قاعدة البيانات)
// ═══════════════════════════════════════════════════════════════════════════
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<PlatformDbContext>()   // ← لا تُفقد أبداً
    .SetApplicationName("ChatPlatform");

// ═══════════════════════════════════════════════════════════════════════════
// 3. النواة والمحرك
// ═══════════════════════════════════════════════════════════════════════════
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<CoreEngine>();

// ═══════════════════════════════════════════════════════════════════════════
// 4. التكاملات (المحولات)
// ═══════════════════════════════════════════════════════════════════════════
builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, ChatAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, UniversalConnectorAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();

// ═══════════════════════════════════════════════════════════════════════════
// 5. الكنترولرات (WebhookController + WhatsAppOnboardingController)
// ═══════════════════════════════════════════════════════════════════════════
builder.Services.AddControllers();

// ═══════════════════════════════════════════════════════════════════════════
// 6. شبكة الإنترنت والمرونة (Polly + HttpClients)
// ═══════════════════════════════════════════════════════════════════════════
builder.Services.AddHttpClient("ResilientClient")
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(
            3,
            retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

builder.Services.AddHttpClient("whatsapp", c =>
    c.BaseAddress = new Uri("https://graph.facebook.com/v21.0/"));

// ═══════════════════════════════════════════════════════════════════════════
// 7. الويب و SignalR
// ═══════════════════════════════════════════════════════════════════════════
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ═══════════════════════════════════════════════════════════════════════════
// 8. بناء التطبيق
// ═══════════════════════════════════════════════════════════════════════════
var app = builder.Build();

// ═══════════════════════════════════════════════════════════════════════════
// 9. تطبيق Migrations (بدلاً من EnsureCreated)
// ═══════════════════════════════════════════════════════════════════════════
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "فشل تطبيق Migrations — تأكد من وجود مجلد Migrations في المشروع.");
        throw;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// 10. Middlewares (الترتيب مهم)
// ═══════════════════════════════════════════════════════════════════════════
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();   // ← يخدم privacy.html / terms.html / data-deletion.html تلقائياً

// ═══════════════════════════════════════════════════════════════════════════
// 11. المسارات (Endpoints)
// ═══════════════════════════════════════════════════════════════════════════

// ─── معلومات المنصة ───
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

// ─── إدارة التكاملات (Connectors) ───
var connectorsGroup = app.MapGroup("/api/connectors");

connectorsGroup.MapGet("/", async (PlatformDbContext db) =>
    Results.Ok(await db.Connectors.AsNoTracking().ToListAsync()));

connectorsGroup.MapPost("/",
    async (ChatPlatform.Infrastructure.Data.Entities.ConnectorEntity req, PlatformDbContext db) =>
    {
        req.Id = Guid.NewGuid().ToString();
        db.Connectors.Add(req);
        await db.SaveChangesAsync();
        return Results.Ok(new { message = "تم الحفظ بنجاح", id = req.Id });
    });

// ─── استقبال Webhook من منصات خارجية عامة ───
app.MapPost("/api/inbound/{platform}",
    async (string platform, HttpRequest request, IEventBus bus) =>
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();

        JsonElement json;
        try
        {
            json = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Invalid JSON body" });
        }

        var evt = new PlatformEvent
        {
            Type = "message.created",
            Source = platform,
            Payload = json
        };
        await bus.PublishAsync(evt);
        return Results.Ok(new { status = "received" });
    });

// ─── نشر الأحداث يدوياً عبر API ───
app.MapPost("/api/events", async (HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    JsonElement json;
    try
    {
        json = JsonDocument.Parse(body).RootElement;
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON body" });
    }

    var eventType = json.TryGetProperty("type", out var t)
        ? t.GetString()
        : "unknown.event";

    var evt = new PlatformEvent
    {
        Type = eventType ?? "unknown.event",
        Source = "http-api",
        Payload = json
    };
    await bus.PublishAsync(evt);
    return Results.Accepted();
});

// ─── Health Check ───
app.MapGet("/health", () => Results.Ok(new
{
    status = "Core is running",
    timestamp = DateTimeOffset.UtcNow
}));

// ─── SignalR Hub ───
app.MapHub<ChatHub>("/hub/chat");

// ═══════════════════════════════════════════════════════════════════════════
// 12. تفعيل الكنترولرات (يجب أن يكون قبل app.Run)
// ═══════════════════════════════════════════════════════════════════════════
app.MapControllers();

app.Run();
