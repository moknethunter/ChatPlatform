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
builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, ChatAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, WebhookAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, MetaAdapter>();
builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ==========================================
// 1. إدارة الإعدادات (Settings API)
// ==========================================
app.MapGet("/api/settings", () => Results.Ok(new {
    DynamicSettings.ServerUrl,
    DynamicSettings.WhatsappPhoneId,
    DynamicSettings.WhatsappToken
}));

app.MapPost("/api/settings", (SettingsRequest req) =>
{
    DynamicSettings.ServerUrl = req.ServerUrl ?? DynamicSettings.ServerUrl;
    DynamicSettings.WhatsappPhoneId = req.WhatsappPhoneId ?? DynamicSettings.WhatsappPhoneId;
    DynamicSettings.WhatsappToken = req.WhatsappToken ?? DynamicSettings.WhatsappToken;
    return Results.Ok(new { message = "تم حفظ الإعدادات بنجاح" });
});

// ==========================================
// 2. تسجيل الأرقام ديناميكياً (Phone Registration)
// ==========================================
app.MapPost("/api/register-phone", (RegisterPhoneRequest req) =>
{
    if (string.IsNullOrEmpty(req.Name) || string.IsNullOrEmpty(req.Phone))
        return Results.BadRequest("الاسم ورقم الهاتف مطلوبان");

    DynamicSettings.RegisteredNumbers[req.Name] = req.Phone;
    return Results.Ok(new { message = "تم تسجيل الرقم", name = req.Name });
});

app.MapGet("/api/registered-phones", () => Results.Ok(DynamicSettings.RegisteredNumbers));

// ==========================================
// 3. استقبال الأحداث (Inbound & Internal)
// ==========================================
app.MapPost("/api/inbound/{platform}", async (string platform, HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest();
    var json = JsonDocument.Parse(body).RootElement;
    
    var evt = new PlatformEvent { Type = "message.created", Source = platform, Payload = json };
    await bus.PublishAsync(evt);
    return Results.Ok(new { status = "received", platform });
});

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

// إدارة الويب هوك
app.MapPost("/api/webhooks", (string name, string url) => {
    WebhookAdapter.RegisteredWebhooks[name] = url;
    return Results.Ok(new { message = "Webhook registered", name, url });
});

app.MapGet("/health", () => Results.Ok(new { status = "Core is running" }));
app.MapHub<ChatHub>("/hub/chat");

app.Run();

// Records
public record SettingsRequest(string? ServerUrl, string? WhatsappPhoneId, string? WhatsappToken);
public record RegisterPhoneRequest(string Name, string Phone);
