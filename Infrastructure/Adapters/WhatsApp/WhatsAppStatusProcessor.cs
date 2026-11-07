using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>
/// يقرأ حالات التسليم الواردة من Meta (sent/delivered/read/failed)
/// ويحدّث حالة الرسائل في WhatsAppMessages، ثم يبث whatsapp.status عبر SignalR.
/// </summary>
public sealed class WhatsAppStatusProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly ILogger<WhatsAppStatusProcessor> _log;

    public WhatsAppStatusProcessor(
        IServiceScopeFactory scopeFactory,
        IEventBus bus,
        ILogger<WhatsAppStatusProcessor> log)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("WhatsAppStatusProcessor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                var delay = processed == 0
                    ? TimeSpan.FromSeconds(3)
                    : TimeSpan.FromMilliseconds(200);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error in status processing loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _log.LogInformation("WhatsAppStatusProcessor stopped");
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var batch = await db.WhatsAppStatusInbox
            .Where(x => !x.Processed)
            .OrderBy(x => x.ReceivedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        foreach (var row in batch)
        {
            try
            {
                var msg = await db.WhatsAppMessages
                    .FirstOrDefaultAsync(m => m.Wamid == row.Wamid, ct);

                if (msg is not null)
                {
                    var normalized = NormalizeStatus(row.Status);
                    if (ShouldUpdate(msg.Status, normalized))
                    {
                        msg.Status = normalized;

                        try
                        {
                            await _bus.PublishAsync(new PlatformEvent
                            {
                                Type = "whatsapp.status",
                                Source = "whatsapp",
                                Payload = JsonSerializer.SerializeToElement(new
                                {
                                    messageId = msg.Id,
                                    conversationId = msg.ConversationId,
                                    wamid = row.Wamid,
                                    status = normalized,
                                    timestampUtc = DateTime.UtcNow
                                })
                            }, ct);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex,
                                "Failed to publish whatsapp.status for {Wamid}", row.Wamid);
                        }
                    }
                }

                row.Processed = true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to process status row {Id}", row.Id);
                row.Processed = true; // تجنب الحلقة اللانهائية
            }
        }

        await db.SaveChangesAsync(ct);
        return batch.Count;
    }

    private static string NormalizeStatus(string metaStatus) =>
        (metaStatus ?? "").ToLowerInvariant() switch
        {
            "sent"      => "Sent",
            "delivered" => "Delivered",
            "read"      => "Read",
            "failed"    => "Failed",
            "deleted"   => "Deleted",
            _           => metaStatus ?? "Unknown"
        };

    private static bool ShouldUpdate(string? current, string incoming)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Queued"]    = 0,
            ["Sent"]      = 1,
            ["Delivered"] = 2,
            ["Read"]      = 3,
            ["Failed"]    = 4,
            ["Deleted"]   = 5
        };

        if (current == null) return true;
        if (incoming == "Failed") return true;

        var curRank = order.TryGetValue(current,  out var c) ? c : -1;
        var incRank = order.TryGetValue(incoming, out var i) ? i : -1;
        return incRank > curRank;
    }
}
