/*using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Hubs;
using ChatPlatform.Infrastructure.Adapters;
using ChatPlatform.Infrastructure.Engine;
using ChatPlatform.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);

// 1. النواة الأساسية
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<CoreEngine>();

// 2. تسجيل التكاملات (هنا نضيف الإضافات الجديدة دون المساس بالنواة)
builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, ChatAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, WebhookAdapter>();
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(5));

// 3. خدمات الويب
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/events", async (HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest("Empty payload");

    var json = JsonDocument.Parse(body).RootElement;
    var eventType = json.TryGetProperty("type", out var t) ? t.GetString() : "unknown.event";
    
    var evt = new PlatformEvent
    {
        Type = eventType ?? "unknown.event",
        Source = "http-api",
        Payload = json
    };

    await bus.PublishAsync(evt);
    return Results.Accepted(value: new { status = "queued", eventId = evt.Id });
});

// نقطة نهاية مؤقتة لإضافة رابط ويب هوك
app.MapPost("/api/webhooks", (string url) =>
{
    WebhookAdapter.TargetUrls.Add(url);
    return Results.Ok(new { message = "Webhook added", total = WebhookAdapter.TargetUrls.Count });
});

app.MapGet("/health", () => Results.Ok(new { status = "Core is running" }));
app.MapHub<ChatHub>("/hub/chat");

app.Run();
*/
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Hubs;
using ChatPlatform.Infrastructure.Adapters;
using ChatPlatform.Infrastructure.Engine;
using ChatPlatform.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);

// 1. النواة الأساسية
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<CoreEngine>();

// 2. تسجيل التكاملات
builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, ChatAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, WebhookAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, MetaAdapter>();
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));

// 3. خدمات الويب
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ==========================================
// 1. استقبال الأحداث من الأنظمة الخارجية (Inbound Webhooks)
// ==========================================
// واتساب/فيسبوك يرسلون رسائلهم هنا
app.MapPost("/api/inbound/{platform}", async (string platform, HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest();

    var json = JsonDocument.Parse(body).RootElement;
    
    // تحويل رسالة واتساب/فيسبوك إلى حدث موحد داخل النواة
    var evt = new PlatformEvent
    {
        Type = "message.created", // نوع الحدث
        Source = platform,        // المصدر: whatsapp أو facebook
        Payload = json            // البيانات كما جاءت من المنصة
    };

    await bus.PublishAsync(evt);
    return Results.Ok(new { status = "received", platform });
});

// ==========================================
// 2. إدارة الويب هوك الخاص بك (Outbound Webhooks)
// ==========================================
app.MapPost("/api/webhooks", (string name, string url) =>
{
    WebhookAdapter.RegisteredWebhooks[name] = url;
    return Results.Ok(new { message = "Webhook registered", name, url });
});

app.MapGet("/api/webhooks", () => Results.Ok(WebhookAdapter.RegisteredWebhooks));

app.MapDelete("/api/webhooks/{name}", (string name) =>
{
    WebhookAdapter.RegisteredWebhooks.TryRemove(name, out _);
    return Results.Ok(new { message = "Webhook removed" });
});

// ==========================================
// 3. استقبال أحداث من تطبيقك الخاص (Dashboard / API)
// ==========================================
app.MapPost("/api/events", async (HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest();

    var json = JsonDocument.Parse(body).RootElement;
    var eventType = json.TryGetProperty("type", out var t) ? t.GetString() : "unknown.event";
    
    var evt = new PlatformEvent
    {
        Type = eventType ?? "unknown.event",
        Source = "http-api",
        Payload = json
    };

    await bus.PublishAsync(evt);
    return Results.Accepted(value: new { status = "queued", eventId = evt.Id });
});

app.MapGet("/health", () => Results.Ok(new { status = "Core is running" }));
app.MapHub<ChatHub>("/hub/chat");

app.Run();
