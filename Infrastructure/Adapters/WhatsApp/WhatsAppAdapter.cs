using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Persists outgoing WhatsApp events to the database outbox.</summary>
public sealed class WhatsAppAdapter : IIntegrationAdapter
{
    public const string InboundType = "whatsapp.inbound";
    public const string OutboundType = "whatsapp.send";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<WhatsAppAdapter> _logger;

    public WhatsAppAdapter(
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        ILogger<WhatsAppAdapter> logger)
    {
        _scopeFactory = scopeFactory;
        _eventBus = eventBus;
        _logger = logger;
    }

    public string Name => "whatsapp";

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (evt.Source?.StartsWith("platform.adapters", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (string.Equals(evt.Type, InboundType, StringComparison.OrdinalIgnoreCase))
        {
            await HandleInboundAsync(evt, ct);
            return;
        }

        if (string.Equals(evt.Type, OutboundType, StringComparison.OrdinalIgnoreCase))
            await HandleOutboundAsync(evt, ct);
    }

    private async Task HandleInboundAsync(PlatformEvent evt, CancellationToken ct)
    {
        // Auto-reply stays opt-in. The inbound event is already persisted as a message.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WhatsAppOptions>>().Value;
        if (!configuration.AutoReply)
            return;

        if (!TryGetString(evt.Payload, "from", out var recipient) ||
            !TryGetString(evt.Payload, "text", out var inboundText) ||
            !TryGetString(evt.Payload, "wamid", out var wamid) ||
            !TryGetString(evt.Payload, "phoneNumberId", out var phoneNumberId) ||
            !TryGetString(evt.Payload, "tenantId", out var tenantId))
            return;

        var reply = $"أهلاً بك، تم استلام رسالتك وسيتم الرد عليك قريبًا.";
        await EnqueueTextAsync(db, tenantId, phoneNumberId, recipient, reply, $"auto-reply:{wamid}", null, ct);
    }

    private async Task HandleOutboundAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (!TryGetString(evt.Payload, "to", out var recipient) ||
            !TryGetString(evt.Payload, "text", out var text) ||
            !TryGetString(evt.Payload, "phoneNumberId", out var phoneNumberId))
        {
            _logger.LogWarning("whatsapp.send event {EventId} is missing to, text, or phoneNumberId", evt.Id);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenantId = await db.WhatsAppTenants
            .AsNoTracking()
            .Where(x => x.PhoneNumberId == phoneNumberId && x.IsActive)
            .Select(x => x.TenantId)
            .SingleOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning("No active WhatsApp tenant for phone number {PhoneNumberId}", phoneNumberId);
            return;
        }

        await EnqueueTextAsync(db, tenantId, phoneNumberId, recipient, text,
            $"event:{evt.Id:N}", null, ct);
    }

    private static async Task EnqueueTextAsync(
        PlatformDbContext db,
        string tenantId,
        string phoneNumberId,
        string recipient,
        string text,
        string idempotencyKey,
        Guid? messageId,
        CancellationToken ct)
    {
        var exists = await db.WhatsAppOutbox.AnyAsync(
            x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, ct);
        if (exists)
            return;

        var now = DateTime.UtcNow;
        db.WhatsAppOutbox.Add(new WhatsAppOutboxEntity
        {
            TenantId = tenantId,
            IdempotencyKey = idempotencyKey,
            MessageId = messageId,
            PhoneNumberId = phoneNumberId,
            RecipientPhone = recipient,
            MessageType = "text",
            PayloadJson = JsonSerializer.Serialize(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = recipient,
                type = "text",
                text = new { preview_url = false, body = text }
            }),
            Status = "Pending",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
            UpdatedAtUtc = now
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Unique (TenantId, IdempotencyKey) index is the concurrency authority.
            db.ChangeTracker.Clear();
            if (!await db.WhatsAppOutbox.AnyAsync(
                    x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, ct))
                throw;
        }
    }

    private static bool TryGetString(JsonElement payload, string key, out string value)
    {
        value = "";
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(key, out var item) ||
            item.ValueKind != JsonValueKind.String)
            return false;

        value = item.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }
}
