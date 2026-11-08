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

/// <summary>
/// Registers WhatsApp adapter services and maps the Webhook endpoints.
/// - GET  /webhook : Meta verification handshake (hub.challenge).
/// - POST /webhook : Receives inbound messages and delivery-status updates.
///
/// Every incoming item is persisted into:
///   • WhatsAppWebhookInbox  — inbound messages
///   • WhatsAppStatusInbox   — delivery/read receipts
/// The corresponding background workers then process them asynchronously.
/// </summary>
public static class WhatsAppExtensions
{
    private const string VerifyTokenEnvVar   = "WHATSAPP_VERIFY_TOKEN";
    private const string AppSecretEnvVar     = "WHATSAPP_APP_SECRET";
    private const string PhoneNumberIdEnvVar = "WHATSAPP_PHONE_NUMBER_ID";
    private const int MaxWebhookBodyBytes    = 1024 * 1024; // 1 MB — Meta payloads are tiny.

    // ══════════════════════════════════════════════════════════════
    // DI registration
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

        services.AddSingleton<IWhatsAppCloudClient, WhatsAppCloudClient>();
        services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();

        services.AddHostedService<WhatsAppInboxWorker>();
        services.AddHostedService<WhatsAppOutboxWorker>();

        // WhatsAppStatusProcessor: enable after confirming that
        // WhatsAppStatusInboxEntity exposes the field it queries on
        // (e.g., 'Processed' / 'IsProcessed').
        // services.AddHostedService<WhatsAppStatusProcessor>();

        return services;
    }

    // ══════════════════════════════════════════════════════════════
    // Webhook endpoints
    // ══════════════════════════════════════════════════════════════
    public static IEndpointRouteBuilder MapWhatsAppWebhook(
        this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        var verifyToken = Environment.GetEnvironmentVariable(VerifyTokenEnvVar)
                          ?? configuration["WhatsApp:VerifyToken"];
        var appSecret   = Environment.GetEnvironmentVariable(AppSecretEnvVar)
                          ?? configuration["WhatsApp:AppSecret"];

        // ── GET: Meta verification handshake ─────────────────────────
        app.MapGet("/webhook", (HttpContext context) =>
        {
            // Only the verify token is required for the handshake.
            if (string.IsNullOrWhiteSpace(verifyToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var mode      = context.Request.Query["hub.mode"].ToString();
            var token     = context.Request.Query["hub.verify_token"].ToString();
            var challenge = context.Request.Query["hub.challenge"].ToString();

            if (!string.Equals(mode, "subscribe", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "unsupported mode" });

            return string.Equals(token, verifyToken, StringComparison.Ordinal)
                ? Results.Text(challenge, "text/plain")
                : Results.Unauthorized();
        });

        // ── POST: message / status events ────────────────────────────
        app.MapPost("/webhook", async (
            HttpContext context,
            PlatformDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("WhatsApp.Webhook");

            if (string.IsNullOrWhiteSpace(appSecret))
            {
                logger.LogError(
                    "AppSecret is missing. Set {EnvVar} or WhatsApp:AppSecret.",
                    AppSecretEnvVar);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // Defensive: cap body size.
            if (context.Request.ContentLength is long len && len > MaxWebhookBodyBytes)
            {
                logger.LogWarning("Rejected oversized webhook body ({Bytes} bytes).", len);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            // Read raw payload (needed verbatim for signature validation).
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

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid JSON in webhook body.");
                return Results.BadRequest();
            }

            var processed      = 0;
            var processingErrors = 0;

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("entry", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest();

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
                            logger.LogWarning(
                                "Webhook change missing metadata.phone_number_id; skipping.");
                            continue;
                        }

                        var tenant = await db.WhatsAppTenants.AsNoTracking()
                            .Where(t => t.PhoneNumberId == phoneNumberId && t.IsActive)
                            .Select(t => new { t.TenantId })
                            .FirstOrDefaultAsync(cancellationToken);

                        if (tenant is null || string.IsNullOrWhiteSpace(tenant.TenantId))
                        {
                            // Meta will retry for up to 7 days once the operator
                            // configures the tenant (e.g., via WhatsAppSmartLinker
                            // or POST /api/whatsapp/tenants).
                            logger.LogError(
                                "No active tenant for phoneNumberId={PhoneId}. " +
                                "Configure {EnvVar} to match, or seed via /api/whatsapp/tenants.",
                                phoneNumberId, PhoneNumberIdEnvVar);
                            processingErrors++;
                            continue;
                        }

                        // ── Inbound messages ─────────────────────────
                        if (value.TryGetProperty("messages", out var messages) &&
                            messages.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var message in messages.EnumerateArray())
                            {
                                try
                                {
                                    if (await TryPersistInboundAsync(
                                            db, tenant.TenantId, phoneNumberId,
                                            message, logger, cancellationToken))
                                        processed++;
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    logger.LogError(ex,
                                        "Failed to persist inbound message for phoneNumberId={PhoneId}.",
                                        phoneNumberId);
                                    processingErrors++;
                                }
                            }
                        }

                        // ── Delivery / read statuses ────────────────
                        if (value.TryGetProperty("statuses", out var statuses) &&
                            statuses.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var status in statuses.EnumerateArray())
                            {
                                try
                                {
                                    if (await TryPersistStatusAsync(
                                            db, tenant.TenantId, phoneNumberId,
                                            status, logger, cancellationToken))
                                        processed++;
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    logger.LogError(ex,
                                        "Failed to persist status for phoneNumberId={PhoneId}.",
                                        phoneNumberId);
                                    processingErrors++;
                                }
                            }
                        }
                    }
                }
            }

            logger.LogInformation(
                "WhatsApp webhook completed: processed={Processed}, errors={Errors}.",
                processed, processingErrors);

            // Only ask Meta to retry if nothing was stored AND there were
            // actual errors. Pure duplicates return 200 (processed stays 0,
            // errors stays 0 because AnyAsync short-circuits).
            return (processingErrors > 0 && processed == 0)
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Ok();
        });

        return app;
    }

    // ══════════════════════════════════════════════════════════════
    // Persistence helpers
    // ══════════════════════════════════════════════════════════════

    private static async Task<bool> TryPersistInboundAsync(
        PlatformDbContext db,
        string tenantId,
        string phoneNumberId,
        JsonElement message,
        ILogger logger,
        CancellationToken ct)
    {
        var wamid = GetString(message, "id");
        if (string.IsNullOrWhiteSpace(wamid))
        {
            logger.LogWarning("Inbound message missing id; skipping.");
            return false;
        }

        var eventKey = WhatsAppEventKeys.ForMessage(wamid);

        if (await db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, ct))
            return true; // duplicate — already stored

        var row = new WhatsAppWebhookInboxEntity
        {
            TenantId          = tenantId,
            EventKey          = eventKey,
            PhoneNumberId     = phoneNumberId,
            PayloadJson       = message.GetRawText(),
            ReceivedAtUtc     = DateTime.UtcNow,
            NextAttemptAtUtc  = DateTime.UtcNow
        };
        db.WhatsAppWebhookInbox.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Stored inbound wamid={Wamid} from={From} phoneNumberId={PhoneId}.",
                wamid, GetString(message, "from"), phoneNumberId);
            return true;
        }
        catch (DbUpdateException)
        {
            // Unique-index race with a concurrent Meta retry — row exists.
            db.Entry(row).State = EntityState.Detached;
            return await db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, ct);
        }
    }

    private static async Task<bool> TryPersistStatusAsync(
        PlatformDbContext db,
        string tenantId,
        string phoneNumberId,
        JsonElement status,
        ILogger logger,
        CancellationToken ct)
    {
        var wamid         = GetString(status, "id");
        var statusName    = GetString(status, "status");
        var timestampText = GetString(status, "timestamp");

        if (string.IsNullOrWhiteSpace(wamid) ||
            string.IsNullOrWhiteSpace(statusName) ||
            !long.TryParse(timestampText, out var timestamp))
        {
            logger.LogWarning("Status update missing id/status/timestamp; skipping.");
            return false;
        }

        var eventKey = WhatsAppEventKeys.ForStatus(wamid, statusName, timestamp);

        if (await db.WhatsAppStatusInbox.AnyAsync(x => x.EventKey == eventKey, ct))
            return true; // duplicate

        var row = new WhatsAppStatusInboxEntity
        {
            TenantId         = tenantId,
            EventKey         = eventKey,
            PhoneNumberId    = phoneNumberId,
            ContactPhone     = GetString(status, "recipient_id") ?? string.Empty,
            Wamid            = wamid,
            Status           = statusName,
            StatusTimestamp  = timestamp,
            PayloadJson      = status.GetRawText(),
            ReceivedAtUtc    = DateTime.UtcNow
        };
        db.WhatsAppStatusInbox.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Stored status wamid={Wamid} status={Status} phoneNumberId={PhoneId}.",
                wamid, statusName, phoneNumberId);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            return await db.WhatsAppStatusInbox.AnyAsync(x => x.EventKey == eventKey, ct);
        }
    }

    // ══════════════════════════════════════════════════════════════
    // JSON helpers
    // ══════════════════════════════════════════════════════════════

    private static string? GetNestedString(JsonElement element, string parent, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(parent, out var nested)
            ? GetString(nested, property)
            : null;

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
