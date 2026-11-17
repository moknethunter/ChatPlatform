using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Hubs;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Endpoints;

public static class EndpointMapper
{
    public static WebApplication MapAllEndpoints(this WebApplication app, IConfiguration configuration)
    {
        // 1. Platform Info & Health
        app.MapGet(ApiEndpoints.PlatformInfo, (HttpRequest request) =>
        {
            var baseUrl = $"{request.Scheme}://{request.Host}";
            return Results.Ok(new
            {
                status = "Online",
                version = "1.0.0",
                endpoints = new
                {
                    whatsappWebhook = $"{baseUrl}{ApiEndpoints.WhatsAppWebhook}",
                    events = $"{baseUrl}{ApiEndpoints.Events}",
                    connectors = $"{baseUrl}{ApiEndpoints.Connectors}",
                    whatsappOnboarding = $"{baseUrl}{ApiEndpoints.WhatsAppOnboarding}",
                    health = $"{baseUrl}{ApiEndpoints.Health}"
                }
            });
        });

        app.MapGet(ApiEndpoints.Health, () => Results.Ok(new { status = "Core is running", timestamp = DateTimeOffset.UtcNow }));

        // 2. Connectors API
        var connectorsGroup = app.MapGroup(ApiEndpoints.Connectors);
        connectorsGroup.MapGet("/", async (PlatformDbContext db) =>
            Results.Ok(await db.Connectors.AsNoTracking().ToListAsync()));

        connectorsGroup.MapPost("/", async (ConnectorEntity req, PlatformDbContext db) =>
        {
            req.Id = Guid.NewGuid().ToString();
            db.Connectors.Add(req);
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "تم الحفظ بنجاح", id = req.Id });
        });

        // 3. Generic Inbound API
        app.MapPost(ApiEndpoints.InboundByPlatform, async (string platform, HttpRequest request, IEventBus bus, CancellationToken ct) =>
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                await bus.PublishAsync(new PlatformEvent { Type = "message.created", Source = platform, Payload = document.RootElement.Clone() }, ct);
                return Results.Ok(new { status = "received" });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON body" }); }
        });

        // 4. Events API
        app.MapPost(ApiEndpoints.Events, async (HttpRequest request, IEventBus bus, CancellationToken ct) =>
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                var json = document.RootElement;
                var eventType = json.TryGetProperty("type", out var type) ? type.GetString() : "unknown.event";
                await bus.PublishAsync(new PlatformEvent { Type = eventType ?? "unknown.event", Source = "http-api", Payload = json.Clone() }, ct);
                return Results.Accepted();
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON body" }); }
        });

        // 5. SignalR Hub
        app.MapHub<ChatHub>(ApiEndpoints.ChatHub);

        // 6. WhatsApp Webhook (من ملف WhatsAppExtensions)
        app.MapWhatsAppWebhook(configuration);

        // 7. Controllers (إذا كان لديك Controllers أخرى)
        app.MapControllers();

        return app;
    }
}
