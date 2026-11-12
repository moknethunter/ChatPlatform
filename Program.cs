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
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// 1. WhatsApp Environment Configuration
// ============================================================

var whatsappEnvironmentSettings = new Dictionary<string, string?>
{
    ["WhatsApp:AccessToken"] =
        Environment.GetEnvironmentVariable("WHATSAPP_TOKEN"),

    ["WhatsApp:PhoneNumberId"] =
        Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID"),

    ["WhatsApp:VerifyToken"] =
        Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN"),

    ["WhatsApp:AppSecret"] =
        Environment.GetEnvironmentVariable("META_APP_SECRET")
        ?? Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET")
};

builder.Configuration.AddInMemoryCollection(
    whatsappEnvironmentSettings.Where(x => x.Value != null));

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

// WhatsApp services, HTTP client, resolver and workers
// are registered inside AddWhatsAppAdapter.
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

// Important:
// ApiKeyMiddleware must allow Meta webhook requests
// to reach WhatsApp signature validation.
app.UseMiddleware<ApiKeyMiddleware>();

app.UseDefaultFiles();

app.UseStaticFiles();

// ============================================================
// 11. Platform Information
// ============================================================

app.MapGet("/api/platform-info", (HttpRequest request) =>
{
    var baseUrl =
        $"{request.Scheme}://{request.Host}";

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

// ============================================================
// 12. Connectors API
// ============================================================

var connectorsGroup = app.MapGroup("/api/connectors");

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
    "/api/inbound/{platform}",
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
    "/api/events",
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

app.MapGet("/health", () =>
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

app.MapHub<ChatHub>("/hub/chat");

// ============================================================
// 17. WhatsApp Meta Webhook
// ============================================================

// GET  /webhook : Meta verification
// POST /webhook : Incoming messages and statuses
app.MapWhatsAppWebhook(builder.Configuration);

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
