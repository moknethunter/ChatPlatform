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
// 1. WhatsApp Configuration - نسخة واحدة نظيفة، بدون تكرار
// ============================================================
var whatsAppSection = builder.Configuration.GetSection("WhatsApp");

// الأولوية: متغير البيئة في SnapDeploy > appsettings.json
var whatsAppToken =
    Environment.GetEnvironmentVariable("WHATSAPP_TOKEN")
    ?? Environment.GetEnvironmentVariable("WhatsApp__AccessToken")
    ?? whatsAppSection["AccessToken"]
    ?? string.Empty;

var whatsAppPhoneId =
    Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID")
    ?? Environment.GetEnvironmentVariable("WhatsApp__PhoneNumberId")
    ?? whatsAppSection["PhoneNumberId"]
    ?? string.Empty;

var whatsAppVerifyToken =
    Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN")
    ?? Environment.GetEnvironmentVariable("WhatsApp__VerifyToken")
    ?? whatsAppSection["VerifyToken"]
    ?? string.Empty;

var whatsAppAppSecret =
    Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET")
    ?? Environment.GetEnvironmentVariable("WhatsApp__AppSecret")
    ?? whatsAppSection["AppSecret"]
    ?? string.Empty;

var whatsAppApiVersion =
    Environment.GetEnvironmentVariable("WHATSAPP_API_VERSION")
    ?? whatsAppSection["GraphApiVersion"]
    ?? whatsAppSection["ApiVersion"]
    ?? "v26.0";

// قراءة واحدة فقط من Configuration - عشان يطبق [ConfigurationKeyName]
var whatsappOptions = builder.Configuration.GetSection(WhatsAppOptions.SectionName).Get<WhatsAppOptions>() ?? new WhatsAppOptions();

// إذا فيه متغيرات بيئة، هي أولى
if (!string.IsNullOrWhiteSpace(whatsAppToken)) whatsappOptions.AccessToken = whatsAppToken;
if (!string.IsNullOrWhiteSpace(whatsAppPhoneId)) whatsappOptions.PhoneNumberId = whatsAppPhoneId;
if (!string.IsNullOrWhiteSpace(whatsAppVerifyToken)) whatsappOptions.VerifyToken = whatsAppVerifyToken;
if (!string.IsNullOrWhiteSpace(whatsAppAppSecret)) whatsappOptions.AppSecret = whatsAppAppSecret;
if (!string.IsNullOrWhiteSpace(whatsAppApiVersion)) whatsappOptions.ApiVersion = whatsAppApiVersion;

builder.Services.AddSingleton(whatsappOptions);

if (!whatsappOptions.IsConfigured)
{
    Console.WriteLine("⚠️ تحذير: إعدادات واتساب ناقصة. سيشتغل الخادم لكن واتساب لن يعمل حتى تكمل الإعدادات.");
    Console.WriteLine("المفقود: " +
        (string.IsNullOrWhiteSpace(whatsAppToken) ? "[AccessToken] " : "") +
        (string.IsNullOrWhiteSpace(whatsAppPhoneId) ? "[PhoneNumberId] " : "") +
        (string.IsNullOrWhiteSpace(whatsAppVerifyToken) ? "[VerifyToken] " : ""));
}
else
{
    Console.WriteLine($"✅ إعدادات واتساب موجودة - ApiVersion: {whatsappOptions.ApiVersion}");
}

if (string.IsNullOrWhiteSpace(whatsAppAppSecret))
{
    Console.WriteLine("⚠️ تحذير: WhatsApp:AppSecret غير معين. لن يتم التحقق من توقيع X-Hub-Signature-256 من Meta.");
}

// ============================================================
// 2. Database - إصلاح اسم القاعدة + مسار ثابت
// ============================================================
var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration.GetConnectionString("Default")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? "Data Source=/app/data/chat.db";

// تأكد أن مجلد /app/data موجود
try
{
    var dbPath = connectionString.Contains("Data Source=")
        ? connectionString.Split("Data Source=")[1].Split(';')[0].Trim()
        : null;
    if (!string.IsNullOrWhiteSpace(dbPath) && !dbPath.Contains(":"))
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}
catch { }

builder.Services.AddDbContext<PlatformDbContext>(options =>
{
    if (connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
        connectionString.Contains("Data Source=tcp:", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlServer(connectionString);
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});

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
    if (!string.IsNullOrWhiteSpace(whatsAppToken))
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
// 9. Database Initialization + بيانات اختبار تظهر في الصندوق
// ============================================================
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<PlatformDbContext>();

    try
    {
        db.Database.EnsureCreated();

        // --- Seed للاختبار فقط: إذا ما فيه محادثات، عبّي رسائل تجريبية ---
        // نستخدم Default Tenant اللي موجود أصلاً في PlatformDbContext
        // Id = "00000000-0000-0000-0000-000000000001"
        if (!db.WhatsAppConversations.Any())
        {
            var defaultTenantId = TenantEntity.DefaultTenantId;
            var phoneNumberId = whatsAppPhoneId;
            if (string.IsNullOrWhiteSpace(phoneNumberId))
                phoneNumberId = "123456789"; // للاختبار فقط

            // 1. WhatsAppTenant - مطابق 100% لـ WhatsAppTenantEntity.cs
            // Id = string، CreatedAt مو CreatedAtUtc، BusinessTokenEncrypted مو AccessToken
            if (!db.WhatsAppTenants.Any(x => x.PhoneNumberId == phoneNumberId))
            {
                db.WhatsAppTenants.Add(new WhatsAppTenantEntity
                {
                    Id = Guid.NewGuid().ToString(),
                    TenantId = defaultTenantId,
                    PhoneNumberId = phoneNumberId,
                    WabaId = "test-waba-1",
                    AppId = "test-app-1",
                    BusinessTokenEncrypted = "TEST_ENCRYPTED_TOKEN",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                db.SaveChanges();
            }

            // 2. محادثة - أحمد اليمني - مطابق 100% لـ WhatsAppConversationEntity.cs
            var conv1Id = Guid.NewGuid();
            db.WhatsAppConversations.Add(new WhatsAppConversationEntity
            {
                Id = conv1Id,
                TenantId = defaultTenantId,
                PhoneNumberId = phoneNumberId,
                ContactPhone = "967777111222",
                ContactName = "أحمد اليمني",
                Status = "open",
                LastInboundAtUtc = DateTime.UtcNow.AddMinutes(-5),
                LastOutboundAtUtc = DateTime.UtcNow.AddMinutes(-9),
                WindowExpiresAtUtc = DateTime.UtcNow.AddHours(24),
                UnreadCount = 2,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            // 3. محادثة - سارة
            var conv2Id = Guid.NewGuid();
            db.WhatsAppConversations.Add(new WhatsAppConversationEntity
            {
                Id = conv2Id,
                TenantId = defaultTenantId,
                PhoneNumberId = phoneNumberId,
                ContactPhone = "967733444555",
                ContactName = "سارة",
                Status = "open",
                LastInboundAtUtc = DateTime.UtcNow.AddMinutes(-2),
                LastOutboundAtUtc = null,
                WindowExpiresAtUtc = DateTime.UtcNow.AddHours(24),
                UnreadCount = 1,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.SaveChanges();

            // 4. رسائل - مطابقة 100% لـ WhatsAppMessageEntity.cs
            db.WhatsAppMessages.AddRange(
                new WhatsAppMessageEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = defaultTenantId,
                    ConversationId = conv1Id,
                    Wamid = "wamid.test.1",
                    Direction = "Inbound",
                    MessageType = "text",
                    ContentJson = "{\"type\":\"text\",\"text\":{\"body\":\"السلام عليكم، عندكم توصيل للمكلا؟\"}}",
                    TextPreview = "السلام عليكم، عندكم توصيل للمكلا؟",
                    Status = "Received",
                    TimestampUtc = DateTime.UtcNow.AddMinutes(-10),
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
                },
                new WhatsAppMessageEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = defaultTenantId,
                    ConversationId = conv1Id,
                    Wamid = "wamid.test.2",
                    Direction = "Outbound",
                    MessageType = "text",
                    ContentJson = "{\"type\":\"text\",\"text\":{\"body\":\"وعليكم السلام، نعم نوصل للمكلا خلال ساعتين\"}}",
                    TextPreview = "وعليكم السلام، نعم نوصل للمكلا خلال ساعتين",
                    Status = "Sent",
                    TimestampUtc = DateTime.UtcNow.AddMinutes(-9),
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-9)
                },
                new WhatsAppMessageEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = defaultTenantId,
                    ConversationId = conv1Id,
                    Wamid = "wamid.test.3",
                    Direction = "Inbound",
                    MessageType = "text",
                    ContentJson = "{\"type\":\"text\",\"text\":{\"body\":\"هلا، كيف أقدر أطلب؟\"}}",
                    TextPreview = "هلا، كيف أقدر أطلب؟",
                    Status = "Received",
                    TimestampUtc = DateTime.UtcNow.AddMinutes(-5),
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
                },
                new WhatsAppMessageEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = defaultTenantId,
                    ConversationId = conv2Id,
                    Wamid = "wamid.test.4",
                    Direction = "Inbound",
                    MessageType = "text",
                    ContentJson = "{\"type\":\"text\",\"text\":{\"body\":\"شكرا لكم، وصل الطلب\"}}",
                    TextPreview = "شكرا لكم، وصل الطلب",
                    Status = "Received",
                    TimestampUtc = DateTime.UtcNow.AddMinutes(-2),
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
                }
            );
            db.SaveChanges();
            Console.WriteLine("✅ تم إنشاء قاعدة الاختبار: 2 محادثة + 4 رسائل في الصندوق");
        }
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
