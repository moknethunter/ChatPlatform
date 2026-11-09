using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
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
    private const string VerifyTokenEnvVar = "WHATSAPP_VERIFY_TOKEN";
    private const string AppSecretEnvVar = "WHATSAPP_APP_SECRET";
    private const int MaxWebhookBodyBytes = 1024 * 1024;

    // ══════════════════════════════════════════════════════════════
    // DI Registration
    // ══════════════════════════════════════════════════════════════
    public static IServiceCollection AddWhatsAppAdapter(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WhatsAppOptions>(
            configuration.GetSection(WhatsAppOptions.SectionName));

        services.AddHttpClient("whatsapp", client =>
        {
            client.BaseAddress = new Uri("https://graph.facebook.com/v21.0/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();
        services.AddHostedService<WhatsAppInboxWorker>();
        services.AddHostedService<WhatsAppOutboxWorker>();

        return services;
    }

    // ══════════════════════════════════════════════════════════════
    // Webhook Endpoints
    // ══════════════════════════════════════════════════════════════
    public static IEndpointRouteBuilder MapWhatsAppWebhook(
        this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        var verifyToken = Environment.GetEnvironmentVariable(VerifyTokenEnvVar)
                          ?? configuration["WhatsApp:VerifyToken"];
        var appSecret = Environment.GetEnvironmentVariable(AppSecretEnvVar)
                        ?? configuration["WhatsApp:AppSecret"];

        // ── GET: Meta verification handshake ─────────────────────────
        app.MapGet("/webhook", (HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(verifyToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var mode = context.Request.Query["hub.mode"].ToString();
            var token = context.Request.Query["hub.verify_token"].ToString();
            var challenge = context.Request.Query["hub.challenge"].ToString();

            if (!string.Equals(mode, "subscribe", StringComparison.Ordinal))
                return Results.BadRequest();

            if (!string.Equals(token, verifyToken, StringComparison.Ordinal))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(challenge))
                return Results.BadRequest();

            return Results.Text(challenge, "text/plain");
        });

        // ── POST: Inbound messages + statuses ────────────────────────
        app.MapPost("/webhook", async (
            HttpContext context,
            PlatformDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("WhatsApp.Webhook");

            if (string.IsNullOrWhiteSpace(appSecret))
            {
                logger.LogError("WhatsApp AppSecret is not configured.");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (context.Request.ContentLength > MaxWebhookBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            string payload;
            try
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer, cancellationToken);
                payload = Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to read webhook body.");
                return Results.BadRequest();
            }

            var signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!WhatsAppSignatureValidator.Validate(payload, signature, appSecret))
            {
                logger.LogWarning("Rejected webhook with invalid signature.");
                return Results.Unauthorized();
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid webhook JSON.");
                return Results.BadRequest();
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("entry", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest();

                var savedCount = 0;

                try
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("changes", out var changes) ||
                            changes.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var change in changes.EnumerateArray())
                        {
                            if (!change.TryGetProperty("value", out var value) ||
                                value.ValueKind != JsonValueKind.Object)
                                continue;

                            var phoneNumberId = GetNestedString(value, "metadata", "phone_number_id");
                            if (string.IsNullOrWhiteSpace(phoneNumberId))
                            {
                                logger.LogWarning("Webhook missing phone_number_id.");
                                continue;
                            }

                            var tenant = await db.WhatsAppTenants
                                .AsNoTracking()
                                .Where(x => x.PhoneNumberId == phoneNumberId && x.IsActive)
                                .Select(x => new { x.TenantId })
                                .FirstOrDefaultAsync(cancellationToken);

                            if (tenant == null || string.IsNullOrWhiteSpace(tenant.TenantId))
                            {
                                logger.LogError(
                                    "No active tenant for phoneNumberId={PhoneId}.",
                                    phoneNumberId);
                                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                            }

                            // ── Inbound messages ──────────────────────
                            if (value.TryGetProperty("messages", out var messages) &&
                                messages.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var message in messages.EnumerateArray())
                                {
                                    var wamid = GetString(message, "id");
                                    if (string.IsNullOrWhiteSpace(wamid)) continue;

                                    var eventKey = WhatsAppEventKeys.ForMessage(wamid);
                                    if (await db.WhatsAppWebhookInbox
                                            .AnyAsync(x => x.EventKey == eventKey, cancellationToken))
                                        continue;

                                    var now = DateTime.UtcNow;
                                    var row = new WhatsAppWebhookInboxEntity
                                    {
                                        TenantId = tenant.TenantId,
                                        EventKey = eventKey,
                                        PhoneNumberId = phoneNumberId,
                                        PayloadJson = message.GetRawText(),
                                        ReceivedAtUtc = now,
                                        NextAttemptAtUtc = now
                                    };
                                    db.WhatsAppWebhookInbox.Add(row);

                                    try
                                    {
                                        await db.SaveChangesAsync(cancellationToken);
                                        savedCount++;
                                    }
                                    catch (DbUpdateException)
                                    {
                                        db.Entry(row).State = EntityState.Detached;
                                        var dup = await db.WhatsAppWebhookInbox
                                            .AnyAsync(x => x.EventKey == eventKey, cancellationToken);
                                        if (!dup) throw;
                                    }
                                }
                            }

                            // ── Delivery / read statuses ─────────────
                            if (value.TryGetProperty("statuses", out var statuses) &&
                                statuses.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var status in statuses.EnumerateArray())
                                {
                                    var wamid = GetString(status, "id");
                                    var statusName = GetString(status, "status");
                                    var timestampText = GetString(status, "timestamp");

                                    if (string.IsNullOrWhiteSpace(wamid) ||
                                        string.IsNullOrWhiteSpace(statusName) ||
                                        !long.TryParse(timestampText, out var timestamp))
                                        continue;

                                    var eventKey = WhatsAppEventKeys.ForStatus(wamid, statusName, timestamp);
                                    if (await db.WhatsAppStatusInbox
                                            .AnyAsync(x => x.EventKey == eventKey, cancellationToken))
                                        continue;

                                    var statusRow = new WhatsAppStatusInboxEntity
                                    {
                                        TenantId = tenant.TenantId,
                                        EventKey = eventKey,
                                        PhoneNumberId = phoneNumberId,
                                        ContactPhone = GetString(status, "recipient_id") ?? "",
                                        Wamid = wamid,
                                        Status = statusName,
                                        StatusTimestamp = timestamp,
                                        PayloadJson = status.GetRawText(),
                                        ReceivedAtUtc = DateTime.UtcNow
                                    };
                                    db.WhatsAppStatusInbox.Add(statusRow);

                                    try
                                    {
                                        await db.SaveChangesAsync(cancellationToken);
                                        savedCount++;
                                    }
                                    catch (DbUpdateException)
                                    {
                                        db.Entry(statusRow).State = EntityState.Detached;
                                        var dup = await db.WhatsAppStatusInbox
                                            .AnyAsync(x => x.EventKey == eventKey, cancellationToken);
                                        if (!dup) throw;
                                    }
                                }
                            }
                        }
                    }

                    logger.LogInformation("WhatsApp webhook persisted. New events: {Count}", savedCount);
                    return Results.Ok();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "WhatsApp webhook persistence failed.");
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }
        });

        return app;
    }

    // ══════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════
    private static string? GetNestedString(JsonElement element, string parent, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(parent, out var nested)) return null;
        return GetString(nested, property);
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
