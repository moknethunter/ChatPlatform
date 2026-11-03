using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Drains durable webhook deliveries. Retries are persisted, so pending work survives process restarts.</summary>
public sealed class WhatsAppWebhookInboxWorker : BackgroundService
{
    private const int BatchSize = 25;
    private const int MaxAttempts = 12;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WhatsAppWebhookInboxWorker> _logger;

    public WhatsAppWebhookInboxWorker(IServiceScopeFactory scopeFactory, ILogger<WhatsAppWebhookInboxWorker> logger)
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
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WhatsApp webhook inbox polling failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var now = DateTimeOffset.UtcNow;
        var batch = await db.WhatsAppWebhookInbox
            .Where(x => (x.Status == "Pending" || x.Status == "Retry") && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.ReceivedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var delivery in batch)
        {
            ct.ThrowIfCancellationRequested();
            delivery.Status = "Processing";
            delivery.Attempts++;
            await db.SaveChangesAsync(ct);

            try
            {
                await DispatchAsync(delivery.RawBody, bus, ct);
                delivery.Status = "Processed";
                delivery.ProcessedAt = DateTimeOffset.UtcNow;
                delivery.LastError = null;
                delivery.NextAttemptAt = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                delivery.Status = "Retry";
                delivery.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(5);
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                delivery.LastError = ex.GetType().Name;
                if (delivery.Attempts >= MaxAttempts)
                {
                    delivery.Status = "DeadLetter";
                    db.WhatsAppDeadLetters.Add(new WhatsAppDeadLetterEntity
                    {
                        MessageKey = delivery.PayloadHash,
                        Channel = "WebhookInbox",
                        Payload = delivery.RawBody,
                        Error = ex.GetType().Name,
                        DeadLetteredAt = DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    delivery.Status = "Retry";
                    delivery.NextAttemptAt = DateTimeOffset.UtcNow.Add(CalculateDelay(delivery.Attempts));
                }
                _logger.LogWarning(ex, "WhatsApp webhook delivery {InboxId} failed on attempt {Attempt}.", delivery.Id, delivery.Attempts);
            }

            await db.SaveChangesAsync(ct);
        }

        return batch.Count;
    }

    private static TimeSpan CalculateDelay(int attempt)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(attempt, 8)));
        return TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble() * Math.Min(seconds * 0.2, 30));
    }

    private static async Task DispatchAsync(string rawBody, IEventBus bus, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(rawBody, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new JsonException("Webhook entry array is missing.");

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value)) continue;
                var phoneNumberId = GetNestedString(value, "metadata", "phone_number_id");

                if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var message in messages.EnumerateArray())
                    {
                        var text = GetNestedString(message, "text", "body");
                        var from = GetString(message, "from");
                        var messageId = GetString(message, "id");
                        await bus.PublishAsync(new PlatformEvent
                        {
                            Type = WhatsAppAdapter.InboundType,
                            Source = "whatsapp.webhook.inbox",
                            Payload = JsonSerializer.SerializeToElement(new
                            {
                                phoneNumberId,
                                from,
                                text,
                                wamid = messageId,
                                message = message.Clone(),
                                receivedAt = DateTimeOffset.UtcNow
                            })
                        }, ct);
                    }
                }

                if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                {
                    foreach (var status in statuses.EnumerateArray())
                    {
                        await bus.PublishAsync(new PlatformEvent
                        {
                            Type = "whatsapp.status",
                            Source = "whatsapp.webhook.inbox",
                            Payload = JsonSerializer.SerializeToElement(new { phoneNumberId, status = status.Clone() })
                        }, ct);
                    }
                }
            }
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? GetNestedString(JsonElement element, string parent, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(parent, out var nested)
            ? GetString(nested, property) : null;
}
