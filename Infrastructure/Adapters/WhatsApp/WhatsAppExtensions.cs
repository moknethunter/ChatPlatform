using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Infrastructure.Adapters.WhatsApp.Binding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppExtensions
{
    private const int MaxWebhookBodyBytes = 1024 * 1024;

    public static IServiceCollection AddWhatsAppAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));
        
        services.AddHttpClient("whatsapp", client =>
        {
            client.BaseAddress = new Uri("https://graph.facebook.com/v26.0/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<IWhatsAppTenantResolver, WhatsAppTenantResolver>();
        services.AddScoped<IWhatsAppCloudClient, WhatsAppCloudClient>();
        services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();
        services.AddHostedService<WhatsAppInboxWorker>();
        services.AddHostedService<WhatsAppOutboxWorker>();

        return services;
    }

    public static IEndpointRouteBuilder MapWhatsAppWebhook(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        var verifyToken = Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN") ?? configuration["WhatsApp:VerifyToken"];
        var appSecret = Environment.GetEnvironmentVariable("META_APP_SECRET") 
                        ?? Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET") 
                        ?? configuration["WhatsApp:AppSecret"];

        // 1. Meta GET Verification
        app.MapGet("/api/whatsapp/webhook", (HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(verifyToken)) return Results.StatusCode(503);

            var q = context.Request.Query;
            if (q["hub.mode"] != "subscribe" || !FixedTimeStringEquals(q["hub.verify_token"], verifyToken))
                return Results.Unauthorized();

            var challenge = q["hub.challenge"].ToString();
            return string.IsNullOrWhiteSpace(challenge) ? Results.BadRequest() : Results.Text(challenge, "text/plain");
        });

        // 2. Meta POST Webhook
        app.MapPost("/api/whatsapp/webhook", async (
            HttpContext context, PlatformDbContext db, IWhatsAppTenantResolver tenantResolver, 
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("WhatsApp.Webhook");

            if (string.IsNullOrWhiteSpace(appSecret))
            {
                logger.LogError("WhatsApp AppSecret is missing.");
                return Results.StatusCode(503);
            }

            // قراءة الـ Body والتحقق من التوقيع
            var body = await ReadLimitedBodyAsync(context.Request, MaxWebhookBodyBytes, ct);
            if (body == null) return Results.StatusCode(413);

            var signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!WhatsAppSignatureValidator.Validate(body, signature, appSecret))
            {
                logger.LogWarning("Invalid WhatsApp webhook signature.");
                return Results.Unauthorized();
            }

            // معالجة البيانات
            try
            {
                using var document = JsonDocument.Parse(body);
                var savedCount = await ProcessWebhookPayloadAsync(document.RootElement, db, tenantResolver, logger, ct);
                
                logger.LogInformation("WhatsApp webhook saved {Count} events.", savedCount);
                return Results.Ok(new { received = true, saved = savedCount });
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook persistence failed.");
                return Results.StatusCode(503);
            }
        });

        return app;
    }

    // ══════════════════════════════════════════════════════════════
    // منطق معالجة البيانات (مفصول لتقليل التعقيد)
    // ══════════════════════════════════════════════════════════════
    private static async Task<int> ProcessWebhookPayloadAsync(
        JsonElement root, PlatformDbContext db, IWhatsAppTenantResolver tenantResolver, ILogger logger, CancellationToken ct)
    {
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return 0;

        var savedCount = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                    continue;

                var phoneNumberId = value.GetNestedString("metadata", "phone_number_id");
                if (string.IsNullOrWhiteSpace(phoneNumberId)) continue;

                var tenant = await tenantResolver.ResolveByPhoneNumberIdAsync(phoneNumberId, ct);
                if (tenant == null || string.IsNullOrWhiteSpace(tenant.TenantId))
                {
                    logger.LogWarning("Unknown WhatsApp account. PhoneNumberId={PhoneId}", phoneNumberId);
                    continue;
                }

                // معالجة الرسائل الواردة
                if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var msg in messages.EnumerateArray())
                    {
                        var wamid = msg.GetString("id");
                        if (string.IsNullOrWhiteSpace(wamid)) continue;

                        var eventKey = WhatsAppEventKeys.ForMessage(wamid);
                        if (await db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, ct)) continue;

                        var row = new WhatsAppWebhookInboxEntity
                        {
                            TenantId = tenant.TenantId, EventKey = eventKey, PhoneNumberId = phoneNumberId,
                            PayloadJson = msg.GetRawText(), ReceivedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow, Status = "Pending"
                        };

                        db.WhatsAppWebhookInbox.Add(row);
                        if (await SaveEventSafelyAsync(db, row, () => db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, ct), ct))
                            savedCount++;
                    }
                }

                // معالجة حالات التسليم (Statuses)
                if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                {
                    foreach (var status in statuses.EnumerateArray())
                    {
                        var wamid = status.GetString("id");
                        var statusName = status.GetString("status");
                        if (string.IsNullOrWhiteSpace(wamid) || string.IsNullOrWhiteSpace(statusName) || 
                            !long.TryParse(status.GetString("timestamp"), out var timestamp)) continue;

                        var eventKey = WhatsAppEventKeys.ForStatus(wamid, statusName, timestamp);
                        if (await db.WhatsAppStatusInbox.AnyAsync(x => x.EventKey == eventKey, ct)) continue;

                        var statusRow = new WhatsAppStatusInboxEntity
                        {
                            TenantId = tenant.TenantId, EventKey = eventKey, PhoneNumberId = phoneNumberId,
                            ContactPhone = status.GetString("recipient_id") ?? "", Wamid = wamid, Status = statusName,
                            StatusTimestamp = timestamp, PayloadJson = status.GetRawText(), ReceivedAtUtc = DateTime.UtcNow
                        };

                        db.WhatsAppStatusInbox.Add(statusRow);
                        if (await SaveEventSafelyAsync(db, statusRow, () => db.WhatsAppStatusInbox.AnyAsync(x => x.EventKey == eventKey, ct), ct))
                            savedCount++;
                    }
                }
            }
        }

        return savedCount;
    }

    // ══════════════════════════════════════════════════════════════
    // دوال مساعدة (Helpers)
    // ══════════════════════════════════════════════════════════════
    private static async Task<byte[]?> ReadLimitedBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes) return null;

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int total = 0, read;

        while ((read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            total += read;
            if (total > maxBytes) return null;
            await ms.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return ms.ToArray();
    }

    private static async Task<bool> SaveEventSafelyAsync<TEntity>(PlatformDbContext db, TEntity row, Func<Task<bool>> duplicateCheck, CancellationToken ct) where TEntity : class
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            if (await duplicateCheck()) return false;
            throw;
        }
    }

    private static bool FixedTimeStringEquals(string? left, string? right)
    {
        if (left == null || right == null) return false;
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

// ══════════════════════════════════════════════════════════════
// Extension Methods لتبسيط قراءة الـ JSON
// ══════════════════════════════════════════════════════════════
public static class JsonElementExtensions
{
    public static string? GetString(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public static string? GetNestedString(this JsonElement element, string parent, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(parent, out var nested)
            ? nested.GetString(property) : null;
}
