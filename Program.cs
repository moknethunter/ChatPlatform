using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Hubs;
using ChatPlatform.Infrastructure.Adapters;
using ChatPlatform.Infrastructure.Configuration;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Engine;
using ChatPlatform.Infrastructure.Messaging;
using ChatPlatform.Middleware;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PlatformDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=chatplatform.db"));
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("EngineSettings"));

builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddHostedService<CoreEngine>();

builder.Services.AddSingleton<IIntegrationAdapter, ConsoleAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, ChatAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, UniversalConnectorAdapter>();
builder.Services.AddWhatsAppAdapter(builder.Configuration);

builder.Services.AddControllers();

builder.Services.AddHttpClient("ResilientClient")
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    db.Database.EnsureCreated();
}

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/platform-info", (HttpRequest request) => {
    var baseUrl = $"{request.Scheme}://{request.Host}";
    return Results.Ok(new
    {
        status = "Online",
        whatsappWebhook = $"{baseUrl}/webhook",
        telegramWebhook = $"{baseUrl}/api/inbound/telegram"
    });
});

var connectorsGroup = app.MapGroup("/api/connectors");
connectorsGroup.MapGet("/", async (PlatformDbContext db) =>
    Results.Ok(await db.Connectors.AsNoTracking().ToListAsync()));

connectorsGroup.MapPost("/", async (ChatPlatform.Infrastructure.Data.Entities.ConnectorEntity req, PlatformDbContext db) =>
{
    req.Id = Guid.NewGuid().ToString();
    db.Connectors.Add(req);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "تم الحفظ بنجاح" });
});

app.MapPost("/api/inbound/{platform}", async (string platform, HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var json = JsonDocument.Parse(body).RootElement;
    var evt = new PlatformEvent { Type = "message.created", Source = platform, Payload = json };
    await bus.PublishAsync(evt);
    return Results.Ok(new { status = "received" });
});

app.MapPost("/api/events", async (HttpRequest request, IEventBus bus) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var json = JsonDocument.Parse(body).RootElement;
    var eventType = json.TryGetProperty("type", out var t) ? t.GetString() : "unknown.event";
    var evt = new PlatformEvent { Type = eventType ?? "unknown.event", Source = "http-api", Payload = json };
    await bus.PublishAsync(evt);
    return Results.Accepted();
});

app.MapGet("/health", () => Results.Ok(new { status = "Core is running" }));
app.MapHub<ChatHub>("/hub/chat");
app.MapWhatsAppWebhook(app.Configuration);

app.Run();
