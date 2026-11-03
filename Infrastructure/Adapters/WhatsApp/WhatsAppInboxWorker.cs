using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Processes persisted WhatsApp webhook and status events with at-least-once semantics.</summary>
public sealed class WhatsAppInboxWorker : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WhatsAppInboxWorker> _logger;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public WhatsAppInboxWorker(IServiceScopeFactory scopeFactory, ILogger<WhatsAppInboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var didWork = await ProcessOneWebhookAsync(stoppingToken);
                didWork |= await ProcessOneStatusAsync(stoppingToken);

                if (!didWork)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WhatsApp inbox worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneWebhookAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var now = DateTime.UtcNow;

        // Recover expired leases before selecting a candidate.
        await db.WhatsAppWebhookInbox
            .Where(x => x.Status == "Processing" && x.LeaseUntilUtc != null && x.LeaseUntilUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "Pending")
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), cancellationToken);

        var candidate = await db.WhatsAppWebhookInbox
            .Where(x => x.Attempts < x.MaxAttempts &&
                        (x.Status == "Pending" || x.Status == "Retry") &&
                        x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.ReceivedAtUtc)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate == Guid.Empty)
            return false;

        var claimed = await db.WhatsAppWebhookInbox
            .Where(x => x.Id == candidate && x.Attempts < x.MaxAttempts &&
                        (x.Status == "Pending" || x.Status == "Retry") &&
                        x.NextAttemptAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "Processing")
                .SetProperty(x => x.LeaseOwner, _leaseOwner)
                .SetProperty(x => x.LeaseUntilUtc, now.Add(LeaseDuration))
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);

        if (claimed == 0)
            return true;

        var item = await db.WhatsAppWebhookInbox
            .SingleAsync(x => x.Id == candidate, cancellationToken);

        try
        {
            using var payload = JsonDocument.Parse(item.PayloadJson);
            var message = payload.RootElement;
            var from = GetString(message, "from");
            var wamid = GetString(message, "id");

            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(wamid))
                throw new InvalidDataException("Persisted WhatsApp message is missing sender or message id.");

            var conversation = await db.WhatsAppConversations
                .SingleOrDefaultAsync(x =>
                    x.TenantId == item.TenantId &&
                    x.PhoneNumberId == item.PhoneNumberId &&
                    x.ContactPhone == from, cancellationToken);

            var receivedAt = ReadUnixTime(GetString(message, "timestamp")) ?? DateTime.UtcNow;

            if (conversation is null)
            {
                conversation = new WhatsAppConversationEntity
                {
                    TenantId = item.TenantId,
                    PhoneNumberId = item.PhoneNumberId,
                    ContactPhone = from,
                    CreatedAtUtc = receivedAt,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Status = "open"
                };
                db.WhatsAppConversations.Add(conversation);
            }

            var existingMessage = await db.WhatsAppMessages
                .SingleOrDefaultAsync(x => x.Wamid == wamid, cancellationToken);

            if (existingMessage is null)
            {
                var type = GetString(message, "type") ?? "unknown";
                var text = type == "text" && message.TryGetProperty("text", out var textElement)
                    ? GetString(textElement, "body")
                    : null;

                db.WhatsAppMessages.Add(new WhatsAppMessageEntity
                {
                    TenantId = item.TenantId,
                    Conversation = conversation,
                    Wamid = wamid,
                    Direction = "Inbound",
                    MessageType = type,
                    ContentJson = message.GetRawText(),
                    TextPreview = text is null ? null : text[..Math.Min(text.Length, 200)],
                    Status = "Received",
                    TimestampUtc = receivedAt,
                    CreatedAtUtc = DateTime.UtcNow
                });

                conversation.LastInboundAtUtc = receivedAt;
                conversation.WindowExpiresAtUtc = receivedAt.AddHours(24);
                conversation.UnreadCount++;
                conversation.UpdatedAtUtc = DateTime.UtcNow;

                await db.SaveChangesAsync(cancellationToken);

                await bus.PublishAsync(new PlatformEvent
                {
                    Type = WhatsAppAdapter.InboundType,
                    Source = "whatsapp.inbox",
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        from,
                        text,
                        wamid,
                        phoneNumberId = item.PhoneNumberId,
                        tenantId = item.TenantId,
                        messageType = type
                    })
                }, cancellationToken);
            }

            item.Status = "Processed";
            item.ProcessedAtUtc = DateTime.UtcNow;
            item.LeaseOwner = null;
            item.LeaseUntilUtc = null;
            item.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var attempts = item.Attempts;
            item.LastError = Truncate(ex.Message);
            item.LeaseOwner = null;
            item.LeaseUntilUtc = null;

            if (attempts >= item.MaxAttempts)
            {
                item.Status = "Failed";
            }
            else
            {
                item.Status = "Retry";
                var delaySeconds = Math.Min(3600, Math.Pow(2, Math.Min(attempts, 10)));
                item.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
            }

            await db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(ex, "WhatsApp webhook inbox item {EventKey} failed (attempt {Attempt})", item.EventKey, attempts);
            return true;
        }
    }

    private async Task<bool> ProcessOneStatusAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var item = await db.WhatsAppStatusInbox
            .Where(x => !x.Processed)
            .OrderBy(x => x.ReceivedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (item is null)
            return false;

        var message = await db.WhatsAppMessages
            .SingleOrDefaultAsync(x => x.Wamid == item.Wamid, cancellationToken);

        if (message is null)
            return false;

        message.Status = item.Status.ToLowerInvariant() switch
        {
            "sent" => "Sent",
            "delivered" => "Delivered",
            "read" => "Read",
            "failed" => "Failed",
            _ => message.Status
        };

        item.Processed = true;
        item.ProcessedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string Truncate(string value, int maxLength = 2000)
        => value.Length > maxLength ? value[..maxLength] : value;

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? ReadUnixTime(string? timestamp)
        => long.TryParse(timestamp, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : null;
}
