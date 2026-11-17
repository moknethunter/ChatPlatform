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
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// ============================================================
// 1. WhatsApp Configuration - متوافق مع Meta فقط
// ============================================================
var whatsAppSection = builder.Configuration.GetSection("WhatsApp");
var whatsAppToken = whatsAppSection["AccessToken"];
var whatsAppPhoneId = whatsAppSection["PhoneNumberId"];
var whatsAppVerifyToken = whatsAppSection["VerifyToken"];
var whatsAppAppSecret = whatsAppSection["AppSecret"];
var whatsAppApiVersion = whatsAppSection["ApiVersion"] ?? "v26.0";

if (string.IsNullOrWhiteSpace(whatsAppToken) ||
    string.IsNullOrWhiteSpace(whatsAppPhoneId) ||
    string.IsNullOrWhiteSpace(whatsAppVerifyToken))
{
    throw new InvalidOperationException(
        "إعدادات واتساب ناقصة في appsettings.json: يجب تعيين WhatsApp:AccessToken و PhoneNumberId و VerifyToken.");
}

if (string.IsNullOrWhiteSpace(whatsAppAppSecret))
{
    Console.WriteLine("⚠️ تحذير: WhatsApp:AppSecret غير معين. لن يتم التحقق من توقيع X-Hub-Signature-256 من Meta.");
}

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

// HttpClient الرسمي لواتساب - Meta Cloud API
builder.Services.AddHttpClient("WhatsAppCloudApi", client =>
{
    client.BaseAddress = new Uri($"https://graph.facebook.com/{whatsAppApiVersion}/");
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", whatsAppToken);
    client.Timeout = TimeSpan.FromSeconds(30);
});

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

app.UseForwardedHeaders();

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
// 11. All Endpoints - من EndpointMapper فقط، بدون تكرار في Program.cs
// ============================================================
app.MapAllEndpoints(builder.Configuration);

// ============================================================
// 12. Run
// ============================================================
app.Run();

public partial class Program
{
}
