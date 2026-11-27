using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Infrastructure.Adapters.WhatsApp.Binding;
using ChatPlatform.Endpoints; // تمت الإضافة لاستخدام ApiEndpoints
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options; // تمت الإضافة

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppExtensions
{
    private const int MaxWebhookBodyBytes = 1024 * 1024;

    public static IServiceCollection AddWhatsAppAdapter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<WhatsAppOptions>(
            configuration.GetSection(WhatsAppOptions.SectionName));

        var graphApiVersion =
            configuration["WhatsApp:GraphApiVersion"]
            ?? new WhatsAppOptions().GraphApiVersion;

        graphApiVersion = graphApiVersion.Trim('/');

        services.AddHttpClient("whatsapp", client =>
        {
            client.BaseAddress =
                new Uri($"https://graph.facebook.com/{graphApiVersion}/");

            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

        // Never forward business tokens through redirects or to a caller-supplied URL.
        services.AddHttpClient("whatsapp-media", client =>
        {
            client.BaseAddress = new Uri($"https://graph.facebook.com/{graphApiVersion}/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<WhatsAppMediaService>();
        services.AddScoped<ChatPlatform.Infrastructure.Marketing.TemplateApproval>();
        services.AddScoped<IWhatsAppTenantResolver, WhatsAppTenantResolver>();
        services.AddScoped<IWhatsAppCloudClient, WhatsAppCloudClient>();
        services.AddScoped<IEmbeddedSignupAttemptStore, DatabaseSignupAttemptStore>();
        services.AddHttpClient("whatsapp-oauth", client =>
        {
            client.BaseAddress = new Uri($"https://graph.facebook.com/{graphApiVersion}/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
          .RemoveAllLoggers();
        services.AddScoped<WhatsAppOnboardingService>();

        services.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();

        services.AddHostedService<WhatsAppInboxWorker>();
        services.AddHostedService<WhatsAppReceiptWorker>();
        services.AddHostedService<WhatsAppOutboxWorker>();

        return services;
    }

    // تم حذف IConfiguration من المعاملات لأننا نستخدم IOptions
    public static IEndpointRouteBuilder MapWhatsAppWebhook(
        this IEndpointRouteBuilder app)
    {
        // GET /webhook
        // Meta verification handshake.
        app.MapGet(ApiEndpoints.WhatsAppWebhook, (HttpContext context, IOptions<WhatsAppOptions> options) =>
        {
            var verifyToken =
                options.Value.VerifyToken;

            if (string.IsNullOrWhiteSpace(verifyToken))
            {
                verifyToken =
                    Environment.GetEnvironmentVariable(
                        "WHATSAPP_VERIFY_TOKEN")
                    ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(verifyToken))
            {
                return Results.StatusCode(503);
            }

            var query = context.Request.Query;

            var mode = query["hub.mode"].ToString();
            var receivedToken = query["hub.verify_token"].ToString();
            var challenge = query["hub.challenge"].ToString();

            if (!string.Equals(
                    mode,
                    "subscribe",
                    StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            if (!FixedTimeStringEquals(receivedToken, verifyToken))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(challenge))
                return Results.BadRequest();

            return Results.Text(challenge, "text/plain");
        }).AllowAnonymous();

        // POST /webhook
        // Meta incoming messages and statuses.
        app.MapPost(
            ApiEndpoints.WhatsAppWebhook,
            async (
                HttpContext context,
                PlatformDbContext db,
                IWhatsAppTenantResolver tenantResolver,
                IOptions<WhatsAppOptions> options,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                var logger = loggerFactory.CreateLogger("WhatsApp.Webhook");
                var appSecret =
                    options.Value.AppSecret;

                if (string.IsNullOrWhiteSpace(appSecret))
                {
                    appSecret =
                        Environment.GetEnvironmentVariable(
                            "WHATSAPP_APP_SECRET")
                        ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(appSecret))
                {
                    logger.LogError("WhatsApp AppSecret is missing.");
                    return Results.StatusCode(503);
                }

                var body = await ReadLimitedBodyAsync(
                    context.Request,
                    MaxWebhookBodyBytes,
                    ct);

                if (body == null)
                    return Results.StatusCode(413);

                var signature =
                    context.Request.Headers[
                        "X-Hub-Signature-256"].ToString();

                if (!WhatsAppSignatureValidator.Validate(
                        body,
                        signature,
                        appSecret))
                {
                    logger.LogWarning(
                        "Invalid WhatsApp webhook signature.");

                    return Results.Unauthorized();
                }

                try
                {
                    using var document =
                        JsonDocument.Parse(body);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return Results.BadRequest();

                    var savedCount =
                        await ProcessWebhookPayloadAsync(
                            document.RootElement,
                            db,
                            tenantResolver,
                            logger,
                            ct);

                    logger.LogInformation(
                        "WhatsApp webhook saved {Count} events.",
                        savedCount);

                    return Results.Ok(new
                    {
                        received = true,
                        saved = savedCount
                    });
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
                    logger.LogError(
                        ex,
                        "Webhook persistence failed.");

                    return Results.StatusCode(503);
                }
            }).AllowAnonymous();

        return app;
    }

    private static async Task<int> ProcessWebhookPayloadAsync(
        JsonElement root,
        PlatformDbContext db,
        IWhatsAppTenantResolver tenantResolver,
        ILogger logger,
        CancellationToken ct)
    {
        if (!root.TryGetProperty("entry", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var savedCount = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("changes", out var changes)
                || changes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object ||
                    !change.TryGetProperty("value", out var value)
                    || value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var phoneNumberId = value.GetNestedString("metadata", "phone_number_id");

                if (string.IsNullOrWhiteSpace(phoneNumberId))
                    continue;

                var tenant = await tenantResolver.ResolveByPhoneNumberIdAsync(phoneNumberId, ct);

                if (tenant == null || string.IsNullOrWhiteSpace(tenant.TenantId))
                {
                    logger.LogWarning("Unknown WhatsApp account. PhoneNumberId={PhoneId}", phoneNumberId);
                    continue;
                }

                if (value.TryGetProperty("messages", out var messages)
                    && messages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var message in messages.EnumerateArray())
                    {
                        var wamid = message.GetString("id");

                        if (string.IsNullOrWhiteSpace(wamid))
                            continue;

                        var eventKey = WhatsAppEventKeys.ForMessage(wamid);

                        if (await db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, ct))
                        {
                            continue;
                        }

                        var row = new WhatsAppWebhookInboxEntity
                        {
                            TenantId = tenant.TenantId,
                            EventKey = eventKey,
                            PhoneNumberId = phoneNumberId,
                            PayloadJson = message.GetRawText(),
                            ReceivedAtUtc = DateTime.UtcNow,
                            NextAttemptAtUtc = DateTime.UtcNow,
                            Status = "Pending"
                        };

                        db.WhatsAppWebhookInbox.Add(row);

                        if (await SaveEventSafelyAsync(
                                db,
                                row,
                                () => db.WhatsAppWebhookInbox.AnyAsync(x => x.EventKey == eventKey, ct),
                                ct))
                        {
                            savedCount++;
                        }
                    }
                }

                if (value.TryGetProperty("statuses", out var statuses)
                    && statuses.ValueKind == JsonValueKind.Array)
                {
                    foreach (var status in statuses.EnumerateArray())
                    {
                        var wamid = status.GetString("id");
                        var statusName = status.GetString("status");

                        if (string.IsNullOrWhiteSpace(wamid)
                            || string.IsNullOrWhiteSpace(statusName)
                            || !long.TryParse(status.GetString("timestamp"), out var timestamp))
                        {
                            continue;
                        }

                        var eventKey = WhatsAppEventKeys.ForStatus(wamid, statusName, timestamp);

                        if (await db.WhatsAppStatusInbox.AnyAsync(x => x.EventKey == eventKey, ct))
                        {
                            continue;
                        }

                        var statusRow = new WhatsAppStatusInboxEntity
                        {
                            TenantId = tenant.TenantId,
                            EventKey = eventKey,
                            PhoneNumberId = phoneNumberId,
                            ContactPhone = status.GetString("recipient_id") ?? "",
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
                                () => db.WhatsAppStatusInbox.AnyAsync(x => x.EventKey == eventKey, ct),
                                ct))
                        {
                            savedCount++;
                        }
                    }
                }
            }
        }

        return savedCount;
    }

    private static async Task<byte[]?> ReadLimitedBodyAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken ct)
    {
        if (request.ContentLength > maxBytes)
            return null;

        using var memoryStream = new MemoryStream();

        var buffer = new byte[8192];
        var total = 0;
        int read;

        while ((read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            total += read;

            if (total > maxBytes)
                return null;

            await memoryStream.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return memoryStream.ToArray();
    }

    private static async Task<bool> SaveEventSafelyAsync<TEntity>(
        PlatformDbContext db,
        TEntity row,
        Func<Task<bool>> duplicateCheck,
        CancellationToken ct)
        where TEntity : class
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;

            if (await duplicateCheck())
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

        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public static class JsonElementExtensions
{
    public static string? GetString(
        this JsonElement element,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static string? GetNestedString(
        this JsonElement element,
        string parent,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.TryGetProperty(parent, out var nested)
            ? nested.GetString(property)
            : null;
    }
}
