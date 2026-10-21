using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Hubs;
using ChatPlatform.Infrastructure.Adapters;
using ChatPlatform.Infrastructure.Configuration;
using ChatPlatform.Infrastructure.Engine;
using ChatPlatform.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<CoreEngine>();

// تسجيل المحرك العام فقط (بدلاً من العشرات من الملفات)
builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, ChatAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, UniversalConnectorAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, WebhookAdapter>();

builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ==========================================
// 1. إدارة التكاملات (Connectors API)
// ==========================================
app.MapGet("/api/connectors", () => Results.Ok(DynamicSettings.Connectors));

app.MapPost("/api/connectors", (ConnectorConfig req) =>
{
    if (string.IsNullOrEmpty(req.Name) || string.IsNullOrEmpty(req.TargetUrl))
        return Results.BadRequest("الاسم والرابط مطلوبان");

    req.Id = Guid.NewGuid().ToString();
    DynamicSettings.Connectors.Add(req);
    return Results.Ok(new { message = "تم إضافة التكامل بنجاح", id = req.Id });
});

app.MapDelete("/api/connectors/{id}", (string id) =>
{
    var item = DynamicSettings.Connectors.FirstOrDefault(c => c.Id == id);
    if (item != null) DynamicSettings.Connectors.Remove(item);
    return Results.Ok(new { message = "تم الحذف" });
});

// ==========================================
// 2. استقبال الأحداث
// ==========================================
app.MapPost("/api/events", async (HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest();
    var json = JsonDocument.Parse(body).RootElement;
    var eventType = json.TryGetProperty("type", out var t) ? t.GetString() : "unknown.event";
    
    var evt = new PlatformEvent { Type = eventType ?? "unknown.event", Source = "http-api", Payload = json };
    await bus.PublishAsync(evt);
    return Results.Accepted(value: new { status = "queued", eventId = evt.Id });
});

app.MapGet("/health", () => Results.Ok(new { status = "Core is running" }));
app.MapHub<ChatHub>("/hub/chat");

app.Run();
