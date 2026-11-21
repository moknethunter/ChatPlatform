using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Hubs;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatPlatform.Endpoints;

public static class EndpointMapper
{
    public static WebApplication MapAllEndpoints(this WebApplication app, IConfiguration configuration)
    {
        // 1. Platform Info & Health
        app.MapGet(ApiEndpoints.PlatformInfo, (HttpRequest request) =>
        {
            var baseUrl = $"{request.Scheme}://{request.Host}";
            return Results.Ok(new
            {
                status = "Online",
                version = "1.0.0",
                endpoints = new
                {
                    whatsappWebhook = $"{baseUrl}{ApiEndpoints.WhatsAppWebhook}",
                    events = $"{baseUrl}{ApiEndpoints.Events}",
                    connectors = $"{baseUrl}{ApiEndpoints.Connectors}",
                    whatsappOnboarding = $"{baseUrl}{ApiEndpoints.WhatsAppOnboarding}",
                    health = $"{baseUrl}{ApiEndpoints.Health}"
                },
                legal = new
                {
                    privacy = $"{baseUrl}/privacy.html",
                    terms = $"{baseUrl}/terms.html",
                    dataDeletion = $"{baseUrl}/data-deletion.html"
                }
            });
        }).RequireAuthorization("TenantRead");

        app.MapGet(ApiEndpoints.Health, () => Results.Ok(new { status = "Core is running", timestamp = DateTimeOffset.UtcNow })).AllowAnonymous();

        // Readiness checks storage separately from the liveness endpoint.
        app.MapGet("/ready", async (PlatformDbContext db, CancellationToken ct) =>
        {
            if (!await db.Database.CanConnectAsync(ct))
                return Results.StatusCode(503);
            return Results.Ok(new { status = "ready" });
        }).AllowAnonymous();

        // 2. Connectors API
        var connectorsGroup = app.MapGroup(ApiEndpoints.Connectors);
        connectorsGroup.MapGet("/", async (PlatformDbContext db) =>
            Results.Ok(await db.Connectors.AsNoTracking().ToListAsync()));

        connectorsGroup.MapPost("/", async (ConnectorEntity req, PlatformDbContext db) =>
        {
            req.Id = Guid.NewGuid().ToString();
            db.Connectors.Add(req);
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "تم الحفظ بنجاح", id = req.Id });
        });

        // 3. Generic Inbound API
        app.MapPost(ApiEndpoints.InboundByPlatform, async (string platform, HttpRequest request, IEventBus bus, CancellationToken ct) =>
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                await bus.PublishAsync(new PlatformEvent { Type = "message.created", Source = platform, Payload = document.RootElement.Clone() }, ct);
                return Results.Ok(new { status = "received" });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON body" }); }
        });

        // 4. Events API
        app.MapPost(ApiEndpoints.Events, async (HttpRequest request, IEventBus bus, CancellationToken ct) =>
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                var json = document.RootElement;
                var eventType = json.TryGetProperty("type", out var type) ? type.GetString() : "unknown.event";
                await bus.PublishAsync(new PlatformEvent { Type = eventType ?? "unknown.event", Source = "http-api", Payload = json.Clone() }, ct);
                return Results.Accepted();
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON body" }); }
        });

        // 5. SignalR Hub
        app.MapHub<ChatHub>(ApiEndpoints.ChatHub);

        // 6. WhatsApp Webhook
        app.MapWhatsAppWebhook();

        // 7. WhatsApp Meta - حالة Webhook - متوافق مع Meta فقط
        app.MapGet(
            ApiEndpoints.WhatsAppWebhookStatus,
            (IOptions<WhatsAppOptions> options,
             HttpRequest request) =>
        {
            var whatsapp =
                options.Value;

            var token =
                whatsapp.AccessToken;

            var phoneNumberId =
                whatsapp.PhoneNumberId;

            var verifyToken =
                whatsapp.VerifyToken;

            var appSecret =
                whatsapp.AppSecret;

            if (string.IsNullOrWhiteSpace(token))
            {
                token =
                    Environment.GetEnvironmentVariable(
                        "WHATSAPP_TOKEN");
            }

            if (string.IsNullOrWhiteSpace(phoneNumberId))
            {
                phoneNumberId =
                    Environment.GetEnvironmentVariable(
                        "WHATSAPP_PHONE_NUMBER_ID");
            }

            if (string.IsNullOrWhiteSpace(verifyToken))
            {
                verifyToken =
                    Environment.GetEnvironmentVariable(
                        "WHATSAPP_VERIFY_TOKEN");
            }

            if (string.IsNullOrWhiteSpace(appSecret))
            {
                appSecret =
                    Environment.GetEnvironmentVariable(
                        "WHATSAPP_APP_SECRET");
            }

            var forwardedProto =
                request.Headers["X-Forwarded-Proto"]
                    .FirstOrDefault();

            var scheme =
                string.Equals(
                    forwardedProto,
                    "https",
                    StringComparison.OrdinalIgnoreCase)
                    ? "https"
                    : request.Scheme;

            var baseUrl =
                $"{scheme}://{request.Host}";

            return Results.Ok(new
            {
                configured =
                    !string.IsNullOrWhiteSpace(token) &&
                    !string.IsNullOrWhiteSpace(phoneNumberId) &&
                    !string.IsNullOrWhiteSpace(verifyToken),

                phoneNumberId,

                apiVersion =
                    whatsapp.ApiVersion,

                webhookUrl =
                    $"{baseUrl}{ApiEndpoints.WhatsAppWebhook}",

                verifyTokenConfigured =
                    !string.IsNullOrWhiteSpace(verifyToken),

                appSecretConfigured =
                    !string.IsNullOrWhiteSpace(appSecret)
            });
        });

        // 8. WhatsApp Meta - إرسال قالب معتمد - متوافق مع Meta فقط
        app.MapPost(ApiEndpoints.WhatsAppSendTemplate, async (
            HttpRequest request,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                var root = doc.RootElement;

                if (!root.TryGetProperty("to", out var to) ||
                    !root.TryGetProperty("template", out var template))
                    return Results.BadRequest(new { error = "to و template مطلوبان." });

                var cleanTo = to.GetString()?.Trim().TrimStart('+').Replace(" ", "").Replace("-", "");
                if (string.IsNullOrWhiteSpace(cleanTo))
                    return Results.BadRequest(new { error = "رقم المستلم غير صالح." });

                var phoneId = configuration["WhatsApp:PhoneNumberId"];
                if (string.IsNullOrWhiteSpace(phoneId))
                    return Results.BadRequest(new { error = "WhatsApp:PhoneNumberId غير معين." });

                var payload = new
                {
                    messaging_product = "whatsapp",
                    to = cleanTo,
                    type = "template",
                    template = new
                    {
                        name = template.GetProperty("name").GetString(),
                        language = new { code = template.TryGetProperty("language", out var lang) && lang.TryGetProperty("code", out var code) ? code.GetString() ?? "ar" : "ar" },
                        components = template.TryGetProperty("components", out var comp) ? comp : (JsonElement?)null
                    }
                };

                var client = httpFactory.CreateClient("WhatsAppCloudApi");
                var response = await client.PostAsJsonAsync($"{phoneId}/messages", payload, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                    return Results.Json(new { error = JsonDocument.Parse(content).RootElement }, statusCode: (int)response.StatusCode);

                return Results.Ok(JsonDocument.Parse(content).RootElement);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON body" });
            }
        });

        // 9. Controllers
        app.MapControllers();

        return app;
    }
}
