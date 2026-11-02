using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Hubs;
using ChatPlatform.Infrastructure.Adapters;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Configuration;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Engine;
using ChatPlatform.Infrastructure.Messaging;
using ChatPlatform.Middleware;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PlatformDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=chatplatform.db"));

builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("EngineSettings"));

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<PlatformDbContext>()
    .SetApplicationName("ChatPlatform");

builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<CoreEngine>();
builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, ChatAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, UniversalConnectorAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();
builder.Services.AddSingleton<WhatsAppIdempotencyStore>();

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ".global",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 200,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

builder.Services.AddHttpClient("ResilientClient")
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

builder.Services.AddHttpClient("whatsapp", client =>
{
    client.BaseAddress = new Uri("https://graph.facebook.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    db.Database.Migrate();
}

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseRateLimiter();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/platform-info", (HttpRequest request) =>
{
    var baseUrl = $"{request.Scheme}://{request.Host}";
    return Results.Ok(new
    {
        status = "Online",
        version = "1.1.0",
        endpoints = new
        {
            whatsappWebhook = $"{baseUrl}/webhook",
            events = $"{baseUrl}/api/events",
            connectors = $"{baseUrl}/api/connectors",
            whatsappOnboarding = $"{baseUrl}/api/whatsapp/exchange-code",
            whatsappMessages = $"{baseUrl}/api/v1/whatsapp/messages",
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

var connectorsGroup = app.MapGroup("/api/connectors");
connectorsGroup.MapGet("/", async (PlatformDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Connectors.AsNoTracking().ToListAsync(ct)));

connectorsGroup.MapPost("/", async (ConnectorRequest request, PlatformDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.TargetUrl) ||
        string.IsNullOrWhiteSpace(request.PayloadTemplate))
        return Results.BadRequest(new { error = "Name, target URL, and payload template are required." });

    if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var target) ||
        target.Scheme is not ("http" or "https"))
        return Results.BadRequest(new { error = "Target URL must be an absolute HTTP(S) URL." });

    var entity = new ChatPlatform.Infrastructure.Data.Entities.ConnectorEntity
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = request.Name.Trim(),
        TargetUrl = request.TargetUrl,
        HttpMethod = request.HttpMethod is "GET" or "POST" ? request.HttpMethod : "POST",
        AuthHeaderKey = string.IsNullOrWhiteSpace(request.AuthHeaderKey) ? "Authorization" : request.AuthHeaderKey,
        AuthToken = request.AuthToken ?? string.Empty,
        PayloadTemplate = request.PayloadTemplate
    };

    db.Connectors.Add(entity);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/connectors/{entity.Id}", new { entity.Id, entity.Name });
});

app.MapPost("/api/inbound/{platform}", async (string platform, HttpRequest request, IEventBus bus, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(platform) || platform.Length > 64)
        return Results.BadRequest(new { error = "Invalid platform identifier." });

    try
    {
        using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        await bus.PublishAsync(new PlatformEvent
        {
            Type = "message.created",
            Source = platform,
            Payload = doc.RootElement.Clone()
        }, ct);
        return Results.Accepted();
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON body." });
    }
});

app.MapPost("/api/events", async (HttpRequest request, IEventBus bus, CancellationToken ct) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var eventType = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var typeVal)
            ? typeVal.GetString()
            : "unknown.event";

        await bus.PublishAsync(new PlatformEvent
        {
            Type = string.IsNullOrWhiteSpace(eventType) ? "unknown.event" : eventType,
            Source = "http-api",
            Payload = root.Clone()
        }, ct);
        return Results.Accepted();
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON body." });
    }
});

app.MapPost("/api/v1/whatsapp/messages", async (SendWhatsAppTextRequest request, IEventBus bus, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.PhoneNumberId) || string.IsNullOrWhiteSpace(request.To) ||
        string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4096)
        return Results.BadRequest(new { error = "phoneNumberId, to, and text (up to 4096 chars) are required." });

    await bus.PublishAsync(new PlatformEvent
    {
        Type = WhatsAppAdapter.OutboundType,
        Source = "api.v1",
        Payload = JsonSerializer.SerializeToElement(new
        {
            phoneNumberId = request.PhoneNumberId.Trim(),
            to = request.To.Trim(),
            text = request.Text
        })
    }, ct);

    return Results.Accepted(new { status = "queued" });
}).RequireRateLimiting("management");

app.MapHealthChecks("/health");
app.MapHub<ChatHub>("/hub/chat");
app.MapWhatsAppWebhook(app.Configuration);
app.MapControllers();

app.Run();

public record ConnectorRequest(string Name, string TargetUrl, string? HttpMethod, string? AuthHeaderKey, string? AuthToken, string PayloadTemplate);
public record SendWhatsAppTextRequest(string PhoneNumberId, string To, string Text);

public partial class Program { }
