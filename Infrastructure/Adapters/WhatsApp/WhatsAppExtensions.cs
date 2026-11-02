using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Adapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppExtensions
{
    private const int MaxWebhookBytes = 1_048_576;

    public static IEndpointRouteBuilder MapWhatsAppWebhook(
        this IEndpointRouteBuilder app, IConfiguration cfg)
    {
        var verifyToken = cfg["WhatsApp:VerifyToken"];
        var appSecret = Environment.GetEnvironmentVariable("META_APP_SECRET") ?? cfg["WhatsApp:AppSecret"];

        app.MapGet("/webhook", (HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(verifyToken))
                return Results.Problem("Webhook verification is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var mode = ctx.Request.Query["hub.mode"].ToString();
            var token = ctx.Request.Query["hub.verify_token"].ToString();
            var challenge = ctx.Request.Query["hub.challenge"].ToString();
            if (mode == "subscribe" && FixedTimeEquals(token, verifyToken) && !string.IsNullOrEmpty(challenge))
                return Results.Text(challenge, "text/plain");

            return Results.Unauthorized();
        });

        app.MapPost("/webhook", async (
            HttpContext ctx,
            IEventBus bus,
            WhatsAppIdempotencyStore idem,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("WhatsApp.Webhook");
            if (string.IsNullOrWhiteSpace(appSecret))
                return Results.Problem("Webhook signature validation is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
            if (ctx.Request.ContentLength is > MaxWebhookBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            byte[] rawBody;
            try
            {
                rawBody = await ReadBoundedBodyAsync(ctx.Request.Body, ct);
            }
            catch (InvalidDataException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var signature = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!WhatsAppSignatureValidator.Validate(rawBody, signature, appSecret))
            {
                logger.LogWarning("Rejected WhatsApp webhook with an invalid signature.");
                return Results.Unauthorized();
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(rawBody, new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid webhook JSON." });
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest(new { error = "Missing webhook entries." });

                try
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var change in changes.EnumerateArray())
                        {
                            if (!change.TryGetProperty("value", out var value)) continue;
                            var phoneNumberId = value.TryGetProperty("metadata", out var metadata) &&
                                                metadata.TryGetProperty("phone_number_id", out var phone)
                                ? phone.GetString()
                                : null;

                            if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var message in messages.EnumerateArray())
                                {
                                    var wamid = message.TryGetProperty("id", out var id) ? id.GetString() : null;
                                    if (!string.IsNullOrWhiteSpace(wamid) && !idem.TryRegister(wamid))
                                    {
                                        logger.LogInformation("Duplicate WhatsApp message suppressed: {MessageId}", wamid);
                                        continue;
                                    }

                                    await bus.PublishAsync(new PlatformEvent
                                    {
                                        Type = WhatsAppAdapter.InboundType,
                                        Source = "whatsapp",
                                        Payload = JsonSerializer.SerializeToElement(new
                                        {
                                            phoneNumberId,
                                            wamid,
                                            message = message.Clone(),
                                            contact = value.TryGetProperty("contacts", out var contacts) ? contacts.Clone() : (JsonElement?)null,
                                            receivedAt = DateTimeOffset.UtcNow
                                        })
                                    }, ct);
                                }
                            }

                            // Delivery/read/failure notifications are forwarded without being treated as inbound messages.
                            if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                            {
                                await bus.PublishAsync(new PlatformEvent
                                {
                                    Type = "whatsapp.status",
                                    Source = "whatsapp",
                                    Payload = JsonSerializer.SerializeToElement(new { phoneNumberId, statuses = statuses.Clone() })
                                }, ct);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Do not acknowledge if dispatch failed; Meta can retry the webhook.
                    logger.LogError(ex, "WhatsApp webhook dispatch failed.");
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }

            return Results.Ok();
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryTokenAttribute());

        return app;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(Stream body, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (buffer.Length + read > MaxWebhookBytes)
                throw new InvalidDataException("Webhook body exceeds configured maximum size.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return buffer.ToArray();
    }
}
