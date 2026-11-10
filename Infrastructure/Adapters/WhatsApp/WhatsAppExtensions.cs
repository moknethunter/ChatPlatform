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
    private const string VerifyTokenEnvVar = "WHATSAPP_VERIFY_TOKEN";
    private const string AppSecretEnvVar = "WHATSAPP_APP_SECRET";

    private const int MaxWebhookBodyBytes = 1024 * 1024;
    private const int ReadBufferSize = 8192;

    public static IServiceCollection AddWhatsAppAdapter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<WhatsAppOptions>(
            configuration.GetSection(WhatsAppOptions.SectionName));

        services.AddHttpClient("whatsapp", client =>
        {
            client.BaseAddress =
                new Uri("https://graph.facebook.com/v21.0/");

            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<
            IWhatsAppTenantResolver,
            WhatsAppTenantResolver>();

        services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();

        services.AddHostedService<WhatsAppInboxWorker>();
        services.AddHostedService<WhatsAppOutboxWorker>();

        return services;
    }

    public static IEndpointRouteBuilder MapWhatsAppWebhook(
        this IEndpointRouteBuilder app,
        IConfiguration configuration)
    {
        var verifyToken =
            Environment.GetEnvironmentVariable(VerifyTokenEnvVar)
            ?? configuration["WhatsApp:VerifyToken"];

        var appSecret =
            Environment.GetEnvironmentVariable(AppSecretEnvVar)
            ?? configuration["WhatsApp:AppSecret"];

        // Meta verification
        app.MapGet("/webhook", (HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(verifyToken))
                return Results.StatusCode(
                    StatusCodes.Status503ServiceUnavailable);

            var mode = context.Request.Query["hub.mode"].ToString();
            var token = context.Request.Query["hub.verify_token"].ToString();
            var challenge = context.Request.Query["hub.challenge"].ToString();

            if (!string.Equals(
                    mode, "subscribe", StringComparison.Ordinal))
                return Results.BadRequest();

            if (!FixedTimeStringEquals(token, verifyToken))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(challenge))
                return Results.BadRequest();

            return Results.Text(challenge, "text/plain");
        });

        // Incoming messages and delivery statuses
        app.MapPost("/webhook", async (
            HttpContext context,
            PlatformDbContext db,
            IWhatsAppTenantResolver tenantResolver,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger =
                loggerFactory.CreateLogger("WhatsApp.Webhook");

            if (string.IsNullOrWhiteSpace(appSecret))
            {
                logger.LogError(
                    "WhatsApp AppSecret is not configured.");

                return Results.StatusCode(
                    StatusCodes.Status503ServiceUnavailable);
            }

            if (context.Request.ContentLength is > MaxWebhookBodyBytes)
                return Results.StatusCode(
                    StatusCodes.Status413PayloadTooLarge);

            byte[] body;

            try
            {
                body = await ReadLimitedBodyAsync(
                    context.Request,
                    MaxWebhookBodyBytes,
                    cancellationToken);
            }
            catch (InvalidDataException)
            {
                return Results.StatusCode(
                    StatusCodes.Status413PayloadTooLarge);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "Failed to read WhatsApp webhook body.");

                return Results.BadRequest();
            }

            var signature = context.Request.Headers[
                "X-Hub-Signature-256"].ToString();

            if (!WhatsAppSignatureValidator.Validate(
                    body, signature, appSecret))
            {
                logger.LogWarning(
                    "Rejected WhatsApp webhook: invalid signature.");

                return Results.Unauthorized();
            }

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(
                    ex, "Invalid WhatsApp webhook JSON.");

                return Results.BadRequest();
            }

            using (document)
            {
                var root = document.RootElement;

                if (!root.TryGetProperty("entry", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                {
                    return Results.BadRequest();
                }

                var savedCount = 0;

                try
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (!entry.TryGetProperty(
                                "changes", out var changes) ||
                            changes.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var change in changes.EnumerateArray())
                        {
                            if (!change.TryGetProperty(
                                    "value", out var value) ||
                                value.ValueKind != JsonValueKind.Object)
                                continue;

                            var phoneNumberId = GetNestedString(
                                value,
                                "metadata",
                                "phone_number_id");

                            if (string.IsNullOrWhiteSpace(phoneNumberId))
                            {
                                logger.LogWarning(
                                    "Webhook change has no phone_number_id.");

                                continue;
                            }

                            var tenant =
                                await tenantResolver.ResolveByPhoneNumberIdAsync(
                                    phoneNumberId,
                                    cancellationToken);

                            if (tenant == null ||
                                string.IsNullOrWhiteSpace(tenant.TenantId))
                            {
                                logger.LogWarning(
                                    "Ignoring webhook event for unknown or inactive WhatsApp account. PhoneNumberId={PhoneId}",
                                    phoneNumberId);

                                // Do not retry indefinitely for an
                                // account that is not registered.
                                continue;
                            }

                            // Inbound messages
                            if (value.TryGetProperty(
                                    "messages", out var messages) &&
                                messages.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var message in messages.EnumerateArray())
                                {
                                    var wamid = GetString(message, "id");

                                    if (string.IsNullOrWhiteSpace(wamid))
                                        continue;

                                    var eventKey =
                                        WhatsAppEventKeys.ForMessage(wamid);

                                    var exists =
                                        await db.WhatsAppWebhookInbox
                                            .AnyAsync(
                                                x => x.EventKey == eventKey,
                                                cancellationToken);

                                    if (exists)
                                        continue;

                                    var now = DateTime.UtcNow;

                                    var row =
                                        new WhatsAppWebhookInboxEntity
                                        {
                                            TenantId = tenant.TenantId,
                                            EventKey = eventKey,
                                            PhoneNumberId = phoneNumberId,
                                            PayloadJson = message.GetRawText(),
                                            ReceivedAtUtc = now,
                                            NextAttemptAtUtc = now
                                        };

                                    db.WhatsAppWebhookInbox.Add(row);

                                    if (await SaveEventSafelyAsync(
                                            db,
                                            row,
                                            x => db.WhatsAppWebhookInbox.AnyAsync(
                                                e => e.EventKey == eventKey,
                                                cancellationToken),
                                            cancellationToken))
                                    {
                                        savedCount++;
                                    }
                                }
                            }

                            // Delivery and read statuses
                            if (value.TryGetProperty(
                                    "statuses", out var statuses) &&
                                statuses.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var status in statuses.EnumerateArray())
                                {
                                    var wamid = GetString(status, "id");
                                    var statusName = GetString(status, "status");
                                    var timestampText =
                                        GetString(status, "timestamp");

                                    if (string.IsNullOrWhiteSpace(wamid) ||
                                        string.IsNullOrWhiteSpace(statusName) ||
                                        !long.TryParse(
                                            timestampText,
                                            out var timestamp))
                                        continue;

                                    var eventKey =
                                        WhatsAppEventKeys.ForStatus(
                                            wamid,
                                            statusName,
                                            timestamp);

                                    var exists =
                                        await db.WhatsAppStatusInbox
                                            .AnyAsync(
                                                x => x.EventKey == eventKey,
                                                cancellationToken);

                                    if (exists)
                                        continue;

                                    var statusRow =
                                        new WhatsAppStatusInboxEntity
                                        {
                                            TenantId = tenant.TenantId,
                                            EventKey = eventKey,
                                            PhoneNumberId = phoneNumberId,
                                            ContactPhone =
                                                GetString(status, "recipient_id")
                                                ?? "",
                                            Wamid = wamid,
                                            Status = statusName,
                                            StatusTimestamp = timestamp,
                                            PayloadJson = status.GetRawText(),
                                            ReceivedAtUtc = DateTime.UtcNow
                                        };

                                    db.WhatsAppStatusInbox.Add(statusRow);

                                    if (await SaveEventSafelyAsync(
                                            db,
                                            statusRow,
                                            x => db.WhatsAppStatusInbox.AnyAsync(
                                                e => e.EventKey == eventKey,
                                                cancellationToken),
                                            cancellationToken))
                                    {
                                        savedCount++;
                                    }
                                }
                            }
                        }
                    }

                    logger.LogInformation(
                        "WhatsApp webhook persisted. New events: {Count}",
                        savedCount);

                    return Results.Ok();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "WhatsApp webhook persistence failed.");

                    return Results.StatusCode(
                        StatusCodes.Status503ServiceUnavailable);
                }
            }
        });

        return app;
    }

    private static async Task<byte[]> ReadLimitedBodyAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferSize];
        var total = 0;

        while (true)
        {
            var read = await request.Body.ReadAsync(
                chunk.AsMemory(0, chunk.Length),
                cancellationToken);

            if (read == 0)
                break;

            total += read;

            if (total > maxBytes)
                throw new InvalidDataException(
                    "Webhook body exceeds the allowed size.");

            await buffer.WriteAsync(
                chunk.AsMemory(0, read),
                cancellationToken);
        }

        return buffer.ToArray();
    }

    private static async Task<bool> SaveEventSafelyAsync<TEntity>(
        PlatformDbContext db,
        TEntity row,
        Func<TEntity, Task<bool>> duplicateCheck,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;

            if (await duplicateCheck(row))
                return false;

            throw;
        }
    }

    private static bool FixedTimeStringEquals(
        string? left,
        string? right)
    {
        if (left == null || right == null)
            return false;

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(
                   leftBytes,
                   rightBytes);
    }

    private static string? GetNestedString(
        JsonElement element,
        string parent,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(parent, out var nested))
            return null;

        return GetString(nested, property);
    }

    private static string? GetString(
        JsonElement element,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
