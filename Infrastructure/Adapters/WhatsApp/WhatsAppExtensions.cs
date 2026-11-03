using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppExtensions
{
    private const int MaxWebhookBytes = 1024 * 1024;

    public static IServiceCollection AddWhatsAppAdapter(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));
        services.AddHttpClient("whatsapp", client =>
        {
            client.BaseAddress = new Uri("https://graph.facebook.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IWhatsAppCloudClient, WhatsAppCloudClient>();
        services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();
        services.AddHostedService<WhatsAppInboxWorker>();
        services.AddHostedService<WhatsAppOutboxWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapWhatsAppWebhook(
        this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        var verifyToken = Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN")
                          ?? configuration["WhatsApp:VerifyToken"];
        var appSecret = Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET")
                        ?? configuration["WhatsApp:AppSecret"];

        app.MapGet("/webhook", (HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(verifyToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var mode = context.Request.Query["hub.mode"];
            var token = context.Request.Query["hub.verify_token"];
            var challenge = context.Request.Query["hub.challenge"];
            return mode == "subscribe" &&
                   string.Equals(token.ToString(), verifyToken, StringComparison.Ordinal)
                ? Results.Text(challenge.ToString(), "text/plain")
                : Results.Unauthorized();
        });

        app.MapPost("/webhook", async (
            HttpContext context,
            PlatformDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("WhatsApp.Webhook");
            if (string.IsNullOrWhiteSpace(appSecret))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            if (context.Request.ContentLength is long length && length > MaxWebhookBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            try
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                var total = 0;
                while (true)
                {
                    var read = await context.Request.Body.ReadAsync(chunk.AsMemory(), cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > MaxWebhookBytes)
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                }

                var rawBody = buffer.ToArray();
                var signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
                if (!WhatsAppSignatureValidator.Validate(rawBody, signature, appSecret))
                {
                    logger.LogWarning("Rejected WhatsApp webhook with invalid signature");
                    return Results.Unauthorized();
                }

                using var document = JsonDocument.Parse(rawBody);
                if (!document.RootElement.TryGetProperty("entry", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest();

                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                            continue;

                        var phoneNumberId = GetNestedString(value, "metadata", "phone_number_id");
                        if (string.IsNullOrWhiteSpace(phoneNumberId))
                            continue;

                        var tenantId = await db.WhatsAppTenants.AsNoTracking()
                            .Where(x => x.PhoneNumberId == phoneNumberId && x.IsActive)
                            .Select(x => x.TenantId)
                            .SingleOrDefaultAsync(cancellationToken);
                        if (string.IsNullOrWhiteSpace(tenantId))
                        {
                            logger.LogWarning("No active tenant for WhatsApp phone number {PhoneNumberId}", phoneNumberId);
                            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                        }

                        if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var message in messages.EnumerateArray())
                            {
                                var wamid = GetString(message, "id");
                                if (string.IsNullOrWhiteSpace(wamid)) continue;
                                var key = WhatsAppEventKeys.ForMessage(wamid);
                                db.WhatsAppWebhookInbox.Add(new WhatsAppWebhookInboxEntity
                                {
                                    TenantId = tenantId,
                                    EventKey = key,
                                    PhoneNumberId = phoneNumberId,
                                    PayloadJson = message.GetRawText(),
                                    ReceivedAtUtc = DateTime.UtcNow,
                                    NextAttemptAtUtc = DateTime.UtcNow
                                });
                                await SaveInboxRowAsync(db, key, cancellationToken);
                            }
                        }

                        if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var status in statuses.EnumerateArray())
                            {
                                var wamid = GetString(status, "id");
                                var statusName = GetString(status, "status");
                                if (string.IsNullOrWhiteSpace(wamid) || string.IsNullOrWhiteSpace(statusName) ||
                                    !long.TryParse(GetString(status, "timestamp"), out var timestamp))
                                    continue;

                                var key = WhatsAppEventKeys.ForStatus(wamid, statusName, timestamp);
                                db.WhatsAppStatusInbox.Add(new WhatsAppStatusInboxEntity
                                {
                                    TenantId = tenantId,
                                    EventKey = key,
                                    PhoneNumberId = phoneNumberId,
                                    ContactPhone = GetString(status, "recipient_id") ?? "",
                                    Wamid = wamid,
                                    Status = statusName,
                                    StatusTimestamp = timestamp,
                                    PayloadJson = status.GetRawText(),
                                    ReceivedAtUtc = DateTime.UtcNow
                                });
                                await SaveStatusRowAsync(db, key, cancellationToken);
                            }
                        }
                    }
                }

                return Results.Ok();
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid JSON in WhatsApp webhook");
                return Results.BadRequest();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to persist WhatsApp webhook; Meta should retry");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });

        return app;
    }

    private static async Task SaveInboxRowAsync(PlatformDbContext db, string key, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == key, ct)) throw;
        }
    }

    private static async Task SaveStatusRowAsync(PlatformDbContext db, string key, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.WhatsAppStatusInbox.AnyAsync(x => x.EventKey == key, ct)) throw;
        }
    }

    private static string? GetNestedString(JsonElement element, string parent, string property)
        => element.TryGetProperty(parent, out var nested) ? GetString(nested, property) : null;

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
