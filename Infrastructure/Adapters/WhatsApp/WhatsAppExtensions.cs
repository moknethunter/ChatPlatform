/*using System.Security.Cryptography;
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
    private const int MaxBodyBytes = 3 * 1024 * 1024;

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

        services.AddSingleton<IWhatsAppCloudClient, WhatsAppCloudClient>();

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
            Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN")
            ?? configuration["WhatsApp:VerifyToken"];

        var appSecret =
            Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET")
            ?? configuration["WhatsApp:AppSecret"];

        // Meta verification handshake
        app.MapGet("/webhook", (HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(verifyToken))
                return Results.StatusCode(503);

            var mode =
                context.Request.Query["hub.mode"].ToString();

            var token =
                context.Request.Query["hub.verify_token"].ToString();

            var challenge =
                context.Request.Query["hub.challenge"].ToString();

            if (mode != "subscribe" ||
                !SecureEquals(token, verifyToken))
                return Results.StatusCode(403);

            return Results.Text(challenge, "text/plain");
        });

        // Meta webhook receiver
        app.MapPost("/webhook", async (
            HttpContext context,
            PlatformDbContext db,
            IWhatsAppTenantResolver tenantResolver,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger =
                loggerFactory.CreateLogger("WhatsApp.Webhook");

            if (string.IsNullOrWhiteSpace(appSecret))
                return Results.StatusCode(503);

            if (context.Request.ContentLength > MaxBodyBytes)
                return Results.StatusCode(413);

            byte[] rawBody;

            try
            {
                using var stream = new MemoryStream();

                var buffer = new byte[81920];
                int count;
                int total = 0;

                while ((count = await context.Request.Body.ReadAsync(
                           buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    total += count;

                    if (total > MaxBodyBytes)
                        return Results.StatusCode(413);

                    await stream.WriteAsync(
                        buffer.AsMemory(0, count), ct);
                }

                rawBody = stream.ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook body read failed.");
                return Results.StatusCode(400);
            }

            var signature =
                context.Request.Headers["X-Hub-Signature-256"].ToString();

            if (!ValidateSignature(rawBody, signature, appSecret))
            {
                logger.LogWarning("Invalid Meta webhook signature.");
                return Results.Unauthorized();
            }

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(rawBody);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            using (document)
            {
                var root = document.RootElement;

                if (!root.TryGetProperty("entry", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest();

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

                            var phoneId = GetNestedString(
                                value,
                                "metadata",
                                "phone_number_id");

                            if (string.IsNullOrWhiteSpace(phoneId))
                                throw new InvalidOperationException(
                                    "Missing metadata.phone_number_id.");

                            // Smart resolution:
                            // An unlinked number must not block webhook storage.
                            var linkedTenant =
                                await tenantResolver
                                    .ResolveByPhoneNumberIdAsync(
                                        phoneId, ct);

                            var tenantId =
                                linkedTenant?.TenantId
                                ?? TenantEntity.DefaultTenantId;

                            await using var transaction =
                                await db.Database.BeginTransactionAsync(ct);

                            if (value.TryGetProperty(
                                    "messages", out var messages) &&
                                messages.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var message in messages.EnumerateArray())
                                {
                                    var wamid = GetString(message, "id");

                                    if (string.IsNullOrWhiteSpace(wamid))
                                        throw new InvalidOperationException(
                                            "Inbound message has no id.");

                                    var key =
                                        WhatsAppEventKeys.ForMessage(wamid);

                                    db.WhatsAppWebhookInbox.Add(
                                        new WhatsAppWebhookInboxEntity
                                        {
                                            TenantId = tenantId,
                                            EventKey = key,
                                            PhoneNumberId = phoneId,
                                            PayloadJson =
                                                message.GetRawText(),
                                            Status = "Pending",
                                            ReceivedAtUtc = DateTime.UtcNow,
                                            NextAttemptAtUtc = DateTime.UtcNow
                                        });
                                }
                            }

                            if (value.TryGetProperty(
                                    "statuses", out var statuses) &&
                                statuses.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var status in statuses.EnumerateArray())
                                {
                                    var wamid = GetString(status, "id");
                                    var name = GetString(status, "status");
                                    var timestampText =
                                        GetString(status, "timestamp");

                                    if (string.IsNullOrWhiteSpace(wamid) ||
                                        string.IsNullOrWhiteSpace(name) ||
                                        !long.TryParse(
                                            timestampText, out var timestamp))
                                        throw new InvalidOperationException(
                                            "Invalid status event.");

                                    var key =
                                        WhatsAppEventKeys.ForStatus(
                                            wamid, name, timestamp);

                                    db.WhatsAppStatusInbox.Add(
                                        new WhatsAppStatusInboxEntity
                                        {
                                            TenantId = tenantId,
                                            EventKey = key,
                                            PhoneNumberId = phoneId,
                                            ContactPhone =
                                                GetString(
                                                    status, "recipient_id")
                                                ?? string.Empty,
                                            Wamid = wamid,
                                            Status = name,
                                            StatusTimestamp = timestamp,
                                            PayloadJson =
                                                status.GetRawText(),
                                            ReceivedAtUtc = DateTime.UtcNow
                                        });
                                }
                            }

                            // Unique indexes provide final deduplication.
                            // Duplicate event keys are handled below.
                            await SaveBatchSafelyAsync(db, ct);

                            await transaction.CommitAsync(ct);

                            if (linkedTenant == null)
                            {
                                logger.LogWarning(
                                    "Webhook stored for unlinked number {PhoneId}.",
                                    phoneId);
                            }
                        }
                    }

                    return Results.Ok();
                }
                catch (DbUpdateException ex)
                {
                    logger.LogWarning(
                        ex,
                        "Webhook persistence conflict or database failure.");

                    return Results.StatusCode(503);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex, "WhatsApp webhook processing failed.");

                    return Results.StatusCode(503);
                }
            }
        });

        return app;
    }

    private static async Task SaveBatchSafelyAsync(
        PlatformDbContext db,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Do not swallow a duplicate race here.
            // The transaction must roll back so Meta can retry.
            throw;
        }
    }

    private static bool ValidateSignature(
        byte[] body,
        string signature,
        string secret)
    {
        if (string.IsNullOrWhiteSpace(signature) ||
            !signature.StartsWith(
                "sha256=", StringComparison.Ordinal))
            return false;

        try
        {
            var supplied =
                Convert.FromHexString(signature.Substring(7));

            using var hmac = new HMACSHA256(
                Encoding.UTF8.GetBytes(secret));

            var expected = hmac.ComputeHash(body);

            return supplied.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(
                       supplied, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool SecureEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);

        return left.Length == right.Length &&
               CryptographicOperations.FixedTimeEquals(
                   left, right);
    }

    private static string? GetNestedString(
        JsonElement element,
        string parent,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(parent, out var nested))
            return null;

        return GetString(nested, property);
    }

    private static string? GetString(
        JsonElement element,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
*/


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
    public static IServiceCollection AddWhatsAppAdapter(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));
        services.AddHttpClient("whatsapp", client =>
        {
            client.BaseAddress = new Uri("https://graph.facebook.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();
        services.AddHostedService<WhatsAppInboxWorker>();
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
            if (string.IsNullOrWhiteSpace(verifyToken) || string.IsNullOrWhiteSpace(appSecret))
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

            try
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer, cancellationToken);
                var payloadBytes = buffer.ToArray();
                var payload = Encoding.UTF8.GetString(payloadBytes);
                var signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
                if (!WhatsAppSignatureValidator.Validate(payload, signature, appSecret))
                {
                    logger.LogWarning("Rejected WhatsApp webhook with invalid signature");
                    return Results.Unauthorized();
                }

                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("entry", out var entries) ||
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
                            continue;

                        var tenant = await db.WhatsAppTenants.AsNoTracking()
                            .Where(item => item.PhoneNumberId == phoneNumberId && item.IsActive)
                            .Select(item => new { item.TenantId })
                            .SingleOrDefaultAsync(cancellationToken);
                        if (tenant is null || string.IsNullOrWhiteSpace(tenant.TenantId))
                        {
                            logger.LogWarning("No active tenant configured for WhatsApp phone number {PhoneNumberId}", phoneNumberId);
                            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                        }

                        if (value.TryGetProperty("messages", out var messages) &&
                            messages.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var message in messages.EnumerateArray())
                            {
                                var wamid = GetString(message, "id");
                                if (string.IsNullOrWhiteSpace(wamid))
                                {
                                    logger.LogWarning("Ignoring WhatsApp message without an id");
                                    continue;
                                }

                                var eventKey = WhatsAppEventKeys.ForMessage(wamid);
                                if (await db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, cancellationToken))
                                    continue;

                                db.WhatsAppWebhookInbox.Add(new WhatsAppWebhookInboxEntity
                                {
                                    TenantId = tenant.TenantId,
                                    EventKey = eventKey,
                                    PhoneNumberId = phoneNumberId,
                                    PayloadJson = message.GetRawText(),
                                    ReceivedAtUtc = DateTime.UtcNow,
                                    NextAttemptAtUtc = DateTime.UtcNow
                                });
                                await SaveInboxRowAsync(db, eventKey, cancellationToken);
                            }
                        }

                        if (value.TryGetProperty("statuses", out var statuses) &&
                            statuses.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var status in statuses.EnumerateArray())
                            {
                                var wamid = GetString(status, "id");
                                var statusName = GetString(status, "status");
                                var timestampText = GetString(status, "timestamp");
                                if (string.IsNullOrWhiteSpace(wamid) || string.IsNullOrWhiteSpace(statusName) ||
                                    !long.TryParse(timestampText, out var timestamp))
                                    continue;

                                var eventKey = WhatsAppEventKeys.ForStatus(wamid, statusName, timestamp);
                                if (await db.WhatsAppStatusInbox.AnyAsync(x => x.EventKey == eventKey, cancellationToken))
                                    continue;

                                db.WhatsAppStatusInbox.Add(new WhatsAppStatusInboxEntity
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
                                });
                                await db.SaveChangesAsync(cancellationToken);
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

    private static async Task SaveInboxRowAsync(
        PlatformDbContext db, string eventKey, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent webhook request may have inserted the same unique EventKey.
            db.ChangeTracker.Clear();
            if (!await db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, cancellationToken))
                throw;
                
        }
    }

    private static string? GetNestedString(JsonElement element, string parent, string property)
        => element.TryGetProperty(parent, out var nested) ? GetString(nested, property) : null;

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

*/
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
    public static IServiceCollection AddWhatsAppAdapter(
        this IServiceCollection services, IConfiguration configuration)
    {
        // 1) الإعدادات
        services.Configure<WhatsAppOptions>(
            configuration.GetSection(WhatsAppOptions.SectionName));

        // 2) HttpClient — ✅ الإصدار في BaseAddress
        services.AddHttpClient("whatsapp", client =>
        {
            client.BaseAddress = new Uri("https://graph.facebook.com/v21.0/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // 3) العميل — 🔴 جديد
        services.AddSingleton<IWhatsAppCloudClient, WhatsAppCloudClient>();

        // 4) المحول
        services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();

        // 5) العمال
        services.AddHostedService<WhatsAppInboxWorker>();
        services.AddHostedService<WhatsAppOutboxWorker>();       // 🔴 جديد
        //services.AddHostedService<WhatsAppStatusProcessor>();    // 🔴 جديد

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
            if (string.IsNullOrWhiteSpace(verifyToken) || string.IsNullOrWhiteSpace(appSecret))
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

            try
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer, cancellationToken);
                var payloadBytes = buffer.ToArray();
                var payload = Encoding.UTF8.GetString(payloadBytes);
                var signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
                if (!WhatsAppSignatureValidator.Validate(payload, signature, appSecret))
                {
                    logger.LogWarning("Rejected WhatsApp webhook with invalid signature");
                    return Results.Unauthorized();
                }

                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("entry", out var entries) ||
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
                            continue;

                        var tenant = await db.WhatsAppTenants.AsNoTracking()
                            .Where(item => item.PhoneNumberId == phoneNumberId && item.IsActive)
                            .Select(item => new { item.TenantId })
                            .SingleOrDefaultAsync(cancellationToken);
                        if (tenant is null || string.IsNullOrWhiteSpace(tenant.TenantId))
                        {
                            logger.LogWarning("No active tenant configured for WhatsApp phone number {PhoneNumberId}", phoneNumberId);
                            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                        }

                        if (value.TryGetProperty("messages", out var messages) &&
                            messages.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var message in messages.EnumerateArray())
                            {
                                var wamid = GetString(message, "id");
                                if (string.IsNullOrWhiteSpace(wamid))
                                {
                                    logger.LogWarning("Ignoring WhatsApp message without an id");
                                    continue;
                                }

                                var eventKey = WhatsAppEventKeys.ForMessage(wamid);
                                if (await db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, cancellationToken))
                                    continue;

                                db.WhatsAppWebhookInbox.Add(new WhatsAppWebhookInboxEntity
                                {
                                    TenantId = tenant.TenantId,
                                    EventKey = eventKey,
                                    PhoneNumberId = phoneNumberId,
                                    PayloadJson = message.GetRawText(),
                                    ReceivedAtUtc = DateTime.UtcNow,
                                    NextAttemptAtUtc = DateTime.UtcNow
                                });
                                await SaveInboxRowAsync(db, eventKey, cancellationToken);
                            }
                        }

                        if (value.TryGetProperty("statuses", out var statuses) &&
                            statuses.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var status in statuses.EnumerateArray())
                            {
                                var wamid = GetString(status, "id");
                                var statusName = GetString(status, "status");
                                var timestampText = GetString(status, "timestamp");
                                if (string.IsNullOrWhiteSpace(wamid) || string.IsNullOrWhiteSpace(statusName) ||
                                    !long.TryParse(timestampText, out var timestamp))
                                    continue;

                                var eventKey = WhatsAppEventKeys.ForStatus(wamid, statusName, timestamp);
                                if (await db.WhatsAppStatusInbox.AnyAsync(x => x.EventKey == eventKey, cancellationToken))
                                    continue;

                                db.WhatsAppStatusInbox.Add(new WhatsAppStatusInboxEntity
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
                                });
                                await db.SaveChangesAsync(cancellationToken);
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

    private static async Task SaveInboxRowAsync(
        PlatformDbContext db, string eventKey, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, cancellationToken))
                throw;
        }
    }

    private static string? GetNestedString(JsonElement element, string parent, string property)
        => element.TryGetProperty(parent, out var nested) ? GetString(nested, property) : null;

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
