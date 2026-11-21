using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Hubs;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
                },
                legal = new
                {
                    privacy = $"{baseUrl}/privacy.html",
                    terms = $"{baseUrl}/terms.html",
                    dataDeletion = $"{baseUrl}/data-deletion.html"
                }
            });
        }).RequireAuthorization("TenantRead");

        app.MapGet(ApiEndpoints.Health, () => Results.Ok(new { status = "Core is running", timestamp = DateTimeOffset.UtcNow, revision = Environment.GetEnvironmentVariable("CHATPLATFORM_REVISION") ?? "unknown" })).AllowAnonymous();

        // Readiness checks storage separately from the liveness endpoint.
        app.MapGet("/ready", async (PlatformDbContext db, CancellationToken ct) =>
        {
            if (!await db.Database.CanConnectAsync(ct))
                return Results.StatusCode(503);
            return Results.Ok(new { status = "ready" });
        }).AllowAnonymous();

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

        // 6. WhatsApp Webhook
        app.MapWhatsAppWebhook();

        // Report configuration for this tenant, never the platform's global access token or phone.
        app.MapGet(ApiEndpoints.WhatsAppWebhookStatus, async (HttpContext ctx, PlatformDbContext db,
            IOptions<WhatsAppOptions> options, CancellationToken ct) =>
        {
            var tenant = ChatPlatform.Security.PlatformSecurity.TenantId(ctx.User);
            var accountConfigured = await db.WhatsAppTenants.AnyAsync(a => a.TenantId == tenant &&
                a.IsActive && a.BusinessTokenEncrypted != "", ct);
            return Results.Ok(new
            {
                configured = accountConfigured,
                apiVersion = options.Value.ApiVersion,
                webhookUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/webhook",
                verifyTokenConfigured = !string.IsNullOrWhiteSpace(options.Value.VerifyToken),
                appSecretConfigured = !string.IsNullOrWhiteSpace(options.Value.AppSecret)
            });
        }).RequireAuthorization("TenantRead");

        // Template sending is implemented by the authenticated durable MessagesController.

        // 9. Controllers
        app.MapControllers();

        return app;
    }
}
