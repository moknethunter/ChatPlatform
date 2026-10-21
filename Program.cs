using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Adapters;
using ChatPlatform.Infrastructure.Engine;
using ChatPlatform.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);

// تسجيل النواة
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<CoreEngine>();

// تسجيل التكاملات (لإضافة تكامل جديد، أضف سطراً هنا فقط)
builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();

var app = builder.Build();

// نقطة نهاية لاستقبال الأحداث من أي مصدر خارجي (سيرفر آخر، منصة أخرى، إلخ)
app.MapPost("/api/events", async (HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest("Empty payload");

    var json = JsonDocument.Parse(body).RootElement;
    
    // افترض أن المصدر يرسل JSON يحتوي على type و data
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

app.Run();
