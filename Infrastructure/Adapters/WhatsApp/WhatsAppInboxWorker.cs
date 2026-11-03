using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Processes persisted WhatsApp webhook and delivery status events.</summary>
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
                var worked = await ProcessOneWebhookAsync(stoppingToken);
                worked |= await ProcessOneStatusAsync(stoppingToken);
                if (!worked) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WhatsApp inbox worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneWebhookAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var now = DateTime.UtcNow;

        var candidate = await db.WhatsAppWebhookInbox.AsNoTracking()
            .Where(x => x.Attempts < x.MaxAttempts &&
                        ((x.Status == "Pending" && x.NextAttemptAtUtc <= now) ||
                         (x.Status == "Retry" && x.NextAttemptAtUtc <= now) ||
                         (x.Status == "Processing" && x.LeaseUntilUtc <= now)))
            .OrderBy(x => x.ReceivedAtUtc)
            .Select(x => new { x.Id })
            .FirstOrDefaultAsync(ct);
        if (candidate is null) return false;

        var claimed = await db.WhatsAppWebhookInbox
            .Where(x => x.Id == candidate.Id && x.Attempts < x.MaxAttempts &&
                        ((x.Status == "Pending" && x.NextAttemptAtUtc <= now) ||
                         (x.Status == "Retry" && x.NextAttemptAtUtc <= now) ||
                         (x.Status == "Processing" && x.LeaseUntilUtc <= now)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "Processing")
                .SetProperty(x => x.LeaseOwner, _leaseOwner)
                .SetProperty(x => x.LeaseUntilUtc, now.Add(LeaseDuration))
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
        if (claimed == 0) return true;

        db.ChangeTracker.Clear();
        var item = await db.WhatsAppWebhookInbox.AsNoTracking()
            .SingleAsync(x => x.Id == candidate.Id, ct);

        try
        {
            using var payload = JsonDocument.Parse(item.PayloadJson);
            var message = payload.RootElement;
            var from = GetString(message, "from");
            var wamid = GetString(message, "id");
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(wamid))
                throw new InvalidDataException("Persisted WhatsApp message is missing sender or message id.");

            var alreadyStored = await db.WhatsAppMessages.AnyAsync(
                x => x.Wamid == wamid && x.TenantId == item.TenantId, ct);
            string? text = null;
            var type = GetString(message, "type") ?? "unknown";

            if (!alreadyStored)
            {
                var receivedAt = ReadUnixTime(GetString(message, "timestamp")) ?? DateTime.UtcNow;
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var conversation = await db.WhatsAppConversations.SingleOrDefaultAsync(x =>
                    x.TenantId == item.TenantId && x.PhoneNumberId == item.PhoneNumberId &&
                    x.ContactPhone == from, ct);

                if (conversation is null)
                {
                    conversation = new WhatsAppConversationEntity
                    {
                        TenantId = item.TenantId,
                        PhoneNumberId = item.PhoneNumberId,
                        ContactPhone = from,
                        CreatedAtUtc = receivedAt,
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    db.WhatsAppConversations.Add(conversation);
                }

                if (type == "text" && message.TryGetProperty("text", out var textElement))
                    text = GetString(textElement, "body");

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
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            // Conditional completion fences out a worker whose lease has been taken over.
            var completed = await db.WhatsAppWebhookInbox
                .Where(x => x.Id == item.Id && x.Status == "Processing" && x.LeaseOwner == _leaseOwner)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "Processed")
                    .SetProperty(x => x.ProcessedAtUtc, DateTime.UtcNow)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.LastError, (string?)null), ct);

            if (completed == 0)
            {
                _logger.LogWarning("Inbox lease lost before completing item {InboxId}", item.Id);
                return true;
            }

            if (!alreadyStored)
            {
                await bus.PublishAsync(new PlatformEvent
                {
                    Type = WhatsAppAdapter.InboundType,
                    Source = "whatsapp.inbox",
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        from, text, wamid,
                        phoneNumberId = item.PhoneNumberId,
                        tenantId = item.TenantId,
                        messageType = type
                    })
                }, ct);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var attempts = item.Attempts + 1;
            var nextStatus = attempts >= item.MaxAttempts ? "Failed" : "Retry";
            var nextAttempt = DateTime.UtcNow.AddSeconds(Math.Min(3600, Math.Pow(2, Math.Min(attempts, 10))));
            var error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

            await db.WhatsAppWebhookInbox
                .Where(x => x.Id == item.Id && x.Status == "Processing" && x.LeaseOwner == _leaseOwner)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, nextStatus)
                    .SetProperty(x => x.NextAttemptAtUtc, nextAttempt)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.LastError, error), ct);

            _logger.LogWarning(ex, "Inbox item {EventKey} failed (attempt {Attempt})", item.EventKey, attempts);
            return true;
        }
    }

    private async Task<bool> ProcessOneStatusAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var item = await db.WhatsAppStatusInbox.AsNoTracking()
            .Where(x => !x.Processed)
            .OrderBy(x => x.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (item is null) return false;

        // Tenant and business phone are both part of the message identity boundary.
        var message = await db.WhatsAppMessages
            .Where(m => m.Wamid == item.Wamid && m.TenantId == item.TenantId &&
                        db.WhatsAppConversations.Any(c => c.Id == m.ConversationId &&
                            c.TenantId == item.TenantId && c.PhoneNumberId == item.PhoneNumberId))
            .Select(m => new { m.Id })
            .SingleOrDefaultAsync(ct);

        if (message is null)
            return false; // Keep status pending; its inbound message may be processed next.

        var status = item.Status.ToLowerInvariant() switch
        {
            "sent" => "Sent",
            "delivered" => "Delivered",
            "read" => "Read",
            "failed" => "Failed",
            _ => null
        };

        if (status is null)
        {
            await db.WhatsAppStatusInbox.Where(x => x.Id == item.Id && !x.Processed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Processed, true)
                    .SetProperty(x => x.ProcessedAtUtc, DateTime.UtcNow), ct);
            return true;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.WhatsAppMessages.Where(x => x.Id == message.Id && x.TenantId == item.TenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
        var marked = await db.WhatsAppStatusInbox.Where(x => x.Id == item.Id && !x.Processed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Processed, true)
                .SetProperty(x => x.ProcessedAtUtc, DateTime.UtcNow), ct);
        await transaction.CommitAsync(ct);
        return marked > 0;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? ReadUnixTime(string? timestamp)
    {
        if (!long.TryParse(timestamp, out var unix)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
