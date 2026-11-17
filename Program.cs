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
using ChatPlatform.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;
using Microsoft.AspNetCore.HttpOverrides; // ⬅️ تمت الإضافة

var builder = WebApplication.CreateBuilder(args);

// ⬇️⬇️ تمت الإضافة: إعدادات Forwarded Headers للتعامل مع بروكسي SnapDeploy ⬇️⬇️
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto;

    // السماح بعكس البروكسي الموجود في SnapDeploy.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
// ⬆️⬆️ ------------------------------------------------------------- ⬆️⬆️

// ============================================================
// 1. WhatsApp Configuration
// ============================================================

// ============================================================
// 2. Database
// ============================================================

builder.Services.AddDbContext<PlatformDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("Default")
        ?? "Data Source=chatplatform.db"));

// ============================================================
// 3. Configuration and Data Protection
// ============================================================

builder.Services.Configure<EngineOptions>(
    builder.Configuration.GetSection("EngineSettings"));

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<PlatformDbContext>()
    .SetApplicationName("ChatPlatform");

// ============================================================
// 4. Event Bus and Core Engine
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

builder.Services.AddHttpClient("ResilientClient")
    .AddPolicyHandler(
        HttpPolicyExtensions.HandleTransientHttpError()
            .WaitAndRetryAsync(
                3,
                retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

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

// ⬇️⬇️ تمت الإضافة: يجب أن يكون أول Middleware في خط الأنابيب ⬇️⬇️
app.UseForwardedHeaders();
// ⬆️⬆️ ------------------------------------------------------- ⬆️⬆️

// ============================================================
// 9. Database Initialization
// ============================================================

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<PlatformDbContext>();

    try
    {
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider
            .GetRequiredService<ILogger<Program>>();

        logger.LogError(
            ex,
            "Failed to initialize ChatPlatform database.");

        throw;
    }
}

// ============================================================
// 10. Middleware Pipeline
// ============================================================

app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseCors();

app.UseMiddleware<ApiKeyMiddleware>();

app.UseDefaultFiles();

app.UseStaticFiles();

// ============================================================
// 11. Platform Information
// ============================================================

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
});

// ============================================================
// 12. Connectors API
// ============================================================

var connectorsGroup = app.MapGroup(ApiEndpoints.Connectors);

connectorsGroup.MapGet("/", async (PlatformDbContext db) =>
{
    var connectors = await db.Connectors
        .AsNoTracking()
        .ToListAsync();

    return Results.Ok(connectors);
});

connectorsGroup.MapPost(
    "/",
    async (ConnectorEntity req, PlatformDbContext db) =>
    {
        req.Id = Guid.NewGuid().ToString();

        db.Connectors.Add(req);

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            message = "تم الحفظ بنجاح",
            id = req.Id
        });
    });

// ============================================================
// 13. Generic Inbound API
// ============================================================

app.MapPost(
    ApiEndpoints.InboundByPlatform,
    async (
        string platform,
        HttpRequest request,
        IEventBus bus,
        CancellationToken ct) =>
    {
        JsonDocument document;

        try
        {
            document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest(
                new { error = "Invalid JSON body" });
        }

        using (document)
        {
            await bus.PublishAsync(
                new PlatformEvent
                {
                    Type = "message.created",
                    Source = platform,
                    Payload = document.RootElement.Clone()
                },
                ct);
        }

        return Results.Ok(new
        {
            status = "received"
        });
    });

// ============================================================
// 14. Events API
// ============================================================

app.MapPost(
    ApiEndpoints.Events,
    async (
        HttpRequest request,
        IEventBus bus,
        CancellationToken ct) =>
    {
        JsonDocument document;

        try
        {
            document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest(
                new { error = "Invalid JSON body" });
        }

        using (document)
        {
            var json = document.RootElement;

            var eventType =
                json.TryGetProperty("type", out var type)
                    ? type.GetString()
                    : "unknown.event";

            await bus.PublishAsync(
                new PlatformEvent
                {
                    Type = eventType ?? "unknown.event",
                    Source = "http-api",
                    Payload = json.Clone()
                },
                ct);
        }

        return Results.Accepted();
    });

// ============================================================
// 15. Health Check
// ============================================================

app.MapGet(ApiEndpoints.Health, () =>
{
    return Results.Ok(new
    {
        status = "Core is running",
        timestamp = DateTimeOffset.UtcNow
    });
});

// ============================================================
// 16. SignalR Hub
// ============================================================

app.MapHub<ChatHub>(ApiEndpoints.ChatHub);

// ============================================================
// 17. WhatsApp Meta Webhook
// ============================================================

app.MapWhatsAppWebhook();

// ============================================================
// 18. Controllers
// ============================================================

app.MapControllers();

// ============================================================
// 19. Run
// ============================================================

app.Run();

public partial class Program
{
}
