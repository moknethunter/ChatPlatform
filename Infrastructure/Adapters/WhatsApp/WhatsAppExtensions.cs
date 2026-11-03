using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppExtensions
{
    private const int MaxWebhookBytes = 1_048_576;

    public static IEndpointRouteBuilder MapWhatsAppWebhook(this IEndpointRouteBuilder app, IConfiguration cfg)
    {
        var verifyToken = Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN") ?? cfg["WhatsApp:VerifyToken"];
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

        app.MapPost("/webhook", async (HttpContext ctx, PlatformDbContext db, ILoggerFactory loggerFactory, CancellationToken ct) =>
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

            string? messageId;
            try
            {
                using var document = JsonDocument.Parse(rawBody, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest(new { error = "Missing webhook entries." });
                messageId = TryGetFirstMessageId(entries);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid webhook JSON." });
            }

            var hash = Convert.ToHexString(SHA256.HashData(rawBody));
            db.WhatsAppWebhookInbox.Add(new WhatsAppWebhookInboxEntity
            {
                PayloadHash = hash,
                MessageId = messageId,
                RawBody = Encoding.UTF8.GetString(rawBody),
                Status = "Pending",
                ReceivedAt = DateTimeOffset.UtcNow
            });

            try
            {
                // Acknowledge Meta only after the signed delivery has been durably committed.
                await db.SaveChangesAsync(ct);
                return Results.Ok();
            }
            catch (DbUpdateException ex)
            {
                db.ChangeTracker.Clear();
                var duplicate = await db.WhatsAppWebhookInbox.AsNoTracking().AnyAsync(x => x.PayloadHash == hash, ct);
                if (duplicate)
                {
                    logger.LogInformation("Duplicate WhatsApp webhook delivery ignored. Hash={PayloadHash}", hash);
                    return Results.Ok();
                }

                logger.LogError(ex, "Could not persist WhatsApp webhook delivery.");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryTokenAttribute());

        return app;
    }

    private static string? TryGetFirstMessageId(JsonElement entries)
    {
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value) ||
                    !value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) continue;
                foreach (var message in messages.EnumerateArray())
                    if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        return id.GetString();
            }
        }
        return null;
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
