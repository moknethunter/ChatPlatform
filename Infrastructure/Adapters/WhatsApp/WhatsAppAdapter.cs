using System.Text.Json;
using System.Text.RegularExpressions;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppAdapter : IIntegrationAdapter
{
    public const string InboundType = "whatsapp.inbound";
    public const string OutboundType = "whatsapp.send";
    public string Name => "whatsapp";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WhatsAppAdapter> _logger;

    public WhatsAppAdapter(IServiceScopeFactory scopeFactory, ILogger<WhatsAppAdapter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken cancellationToken)
    {
        if (evt.Source?.StartsWith("platform.adapters", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (string.Equals(evt.Type, InboundType, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("WhatsApp inbound event received: {EventId}", evt.Id);
            return;
        }

        if (!string.Equals(evt.Type, OutboundType, StringComparison.OrdinalIgnoreCase))
            return;

        var payload = evt.Payload;
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("payload", out var nestedPayload) &&
            nestedPayload.ValueKind == JsonValueKind.Object)
        {
            payload = nestedPayload;
        }

        var phoneNumberId = ReadString(payload, "phoneNumberId");
        var recipient = NormalizePhone(ReadString(payload, "to"));
        var text = ReadString(payload, "text");
        if (string.IsNullOrWhiteSpace(phoneNumberId) ||
            string.IsNullOrWhiteSpace(recipient) ||
            string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Invalid WhatsApp send event {EventId}", evt.Id);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = await db.WhatsAppTenants.AsNoTracking()
            .Where(x => x.PhoneNumberId == phoneNumberId && x.IsActive)
            .Select(x => new { x.TenantId })
            .SingleOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            _logger.LogWarning("No active WhatsApp tenant for phone number ID {PhoneNumberId}", phoneNumberId);
            return;
        }

        var idempotencyKey = $"event:{evt.Id:N}";
        if (await db.WhatsAppOutbox.AnyAsync(
                x => x.TenantId == tenant.TenantId && x.IdempotencyKey == idempotencyKey,
                cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var conversation = await db.WhatsAppConversations.SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.PhoneNumberId == phoneNumberId && x.ContactPhone == recipient,
            cancellationToken);
        if (conversation is null)
        {
            conversation = new WhatsAppConversationEntity
            {
                TenantId = tenant.TenantId,
                PhoneNumberId = phoneNumberId,
                ContactPhone = recipient,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.WhatsAppConversations.Add(conversation);
        }

        var message = new WhatsAppMessageEntity
        {
            TenantId = tenant.TenantId,
            Conversation = conversation,
            Direction = "Outbound",
            MessageType = "text",
            ContentJson = JsonSerializer.Serialize(new { text }),
            TextPreview = text[..Math.Min(text.Length, 200)],
            Status = "Queued",
            TimestampUtc = now,
            CreatedAtUtc = now
        };
        conversation.LastOutboundAtUtc = now;
        conversation.UpdatedAtUtc = now;
        db.WhatsAppMessages.Add(message);

        var payloadJson = JsonSerializer.Serialize(new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = recipient,
            type = "text",
            text = new { preview_url = false, body = text }
        });
        db.WhatsAppOutbox.Add(new WhatsAppOutboxEntity
        {
            TenantId = tenant.TenantId,
            IdempotencyKey = idempotencyKey,
            MessageId = message.Id,
            PhoneNumberId = phoneNumberId,
            RecipientPhone = recipient,
            MessageType = "text",
            PayloadJson = payloadJson,
            Status = "Pending",
            Attempts = 0,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
            UpdatedAtUtc = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var duplicate = await db.WhatsAppOutbox.AnyAsync(
                x => x.TenantId == tenant.TenantId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);
            if (!duplicate)
                throw;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var phone = Regex.Replace(value, @"[^\d]", "");
        return Regex.IsMatch(phone, @"^\d{8,15}$") ? phone : null;
    }
}
