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
