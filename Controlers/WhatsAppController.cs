using System.Text.Json;
using ChatPlatform.Infrastructure.Adapters.WhatsApp.Binding;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ChatPlatform.Controllers;

[ApiController]
[Route("api/whatsapp")]
public sealed class WhatsAppController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IWhatsAppTenantResolver _tenantResolver;
    private readonly ILogger<WhatsAppController> _logger;

    public WhatsAppController(
        PlatformDbContext db,
        IHttpClientFactory httpFactory,
        IWhatsAppTenantResolver tenantResolver,
        ILogger<WhatsAppController> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    // GET: api/whatsapp/webhook
    [HttpGet("webhook")]
    public IActionResult VerifyWebhook(
        [FromQuery(Name = "hub.mode")] string mode,
        [FromQuery(Name = "hub.verify_token")] string verifyToken,
        [FromQuery(Name = "hub.challenge")] string challenge)
    {
        var expected = Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN")
            ?? "mokco-verify-123";

        if (string.Equals(mode, "subscribe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(verifyToken, expected, StringComparison.Ordinal))
        {
            _logger.LogInformation("WhatsApp webhook verified.");
            return Ok(challenge);
        }

        return Forbid();
    }

    // POST: api/whatsapp/webhook
    [HttpPost("webhook")]
    public async Task<IActionResult> ReceiveWebhook(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        try
        {
            if (body.ValueKind != JsonValueKind.Object)
                return BadRequest(new { ok = false, message = "Invalid payload" });

            if (!body.TryGetProperty("entry", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
                return Ok();

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes)
                    || changes.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value)
                        || value.ValueKind != JsonValueKind.Object)
                        continue;

                    var phoneNumberId =
                        GetNestedString(value, "metadata", "phone_number_id");

                    if (string.IsNullOrWhiteSpace(phoneNumberId))
                        continue;

                    var tenant =
                        await _tenantResolver.ResolveByPhoneNumberIdAsync(
                            phoneNumberId, ct);

                    if (tenant == null)
                    {
                        _logger.LogWarning(
                            "Unknown phoneNumberId in webhook: {PhoneNumberId}",
                            phoneNumberId);

                        continue;
                    }

                    // Incoming messages
                    if (value.TryGetProperty("messages", out var messages)
                        && messages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var message in messages.EnumerateArray())
                        {
                            var wamid = GetString(message, "id");
                            var from = GetString(message, "from") ?? "";
                            var type = GetString(message, "type") ?? "text";
                            var text = "";

                            if (message.TryGetProperty("text", out var textNode)
                                && textNode.ValueKind == JsonValueKind.Object
                                && textNode.TryGetProperty("body", out var bodyNode))
                            {
                                text = bodyNode.GetString() ?? "";
                            }

                            if (string.IsNullOrWhiteSpace(wamid))
                                continue;

                            var conversation =
                                await _db.WhatsAppConversations
                                    .FirstOrDefaultAsync(c =>
                                        c.TenantId == tenant.TenantId &&
                                        c.PhoneNumberId == phoneNumberId &&
                                        c.ContactPhone == from, ct);

                            if (conversation == null)
                            {
                                conversation = new WhatsAppConversationEntity
                                {
                                    TenantId = tenant.TenantId,
                                    PhoneNumberId = phoneNumberId,
                                    ContactPhone = from,
                                    Status = "open",
                                    CreatedAtUtc = DateTime.UtcNow,
                                    UpdatedAtUtc = DateTime.UtcNow,
                                    LastInboundAtUtc = DateTime.UtcNow,
                                    WindowExpiresAtUtc =
                                        DateTime.UtcNow.AddHours(24)
                                };

                                _db.WhatsAppConversations.Add(conversation);
                                await _db.SaveChangesAsync(ct);
                            }
                            else
                            {
                                conversation.LastInboundAtUtc = DateTime.UtcNow;
                                conversation.WindowExpiresAtUtc =
                                    DateTime.UtcNow.AddHours(24);

                                conversation.UpdatedAtUtc = DateTime.UtcNow;
                                conversation.Status = "open";
                            }

                            var exists = await _db.WhatsAppMessages
                                .AnyAsync(m => m.Wamid == wamid, ct);

                            if (!exists)
                            {
                                var messageEntity =
                                    new WhatsAppMessageEntity
                                    {
                                        TenantId = tenant.TenantId,
                                        ConversationId = conversation.Id,
                                        Direction = "Inbound",
                                        MessageType = type,
                                        ContentJson = message.GetRawText(),
                                        TextPreview =
                                            string.IsNullOrWhiteSpace(text)
                                                ? $"[{type}]"
                                                : text,
                                        Status = "Received",
                                        Wamid = wamid,
                                        TimestampUtc = DateTime.UtcNow,
                                        CreatedAtUtc = DateTime.UtcNow
                                    };

                                _db.WhatsAppMessages.Add(messageEntity);
                                await _db.SaveChangesAsync(ct);

                                _logger.LogInformation(
                                    "Saved inbound message: tenant={Tenant}, from={From}, type={Type}, wamid={Wamid}",
                                    tenant.TenantId, from, type, wamid);
                            }
                        }
                    }

                    // Message statuses
                    if (value.TryGetProperty("statuses", out var statuses)
                        && statuses.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var status in statuses.EnumerateArray())
                        {
                            var wamid = GetString(status, "id");
                            var statusName = GetString(status, "status");

                            if (!string.IsNullOrWhiteSpace(wamid)
                                && !string.IsNullOrWhiteSpace(statusName))
                            {
                                _logger.LogInformation(
                                    "WhatsApp status: {Wamid} => {Status}",
                                    wamid, statusName);
                            }
                        }
                    }
                }
            }

            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp webhook processing failed.");

            return Ok(new
            {
                ok = false,
                message = "Webhook processed with error"
            });
        }
    }

    // POST: api/whatsapp/save
    [HttpPost("save")]
    public async Task<IActionResult> SaveConnection(
        [FromBody] SaveConnectionDto dto,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.TenantId)
            || string.IsNullOrWhiteSpace(dto.PhoneNumberId)
            || string.IsNullOrWhiteSpace(dto.AccessToken))
        {
            return BadRequest(new
            {
                ok = false,
                message = "TenantId, PhoneNumberId and AccessToken are required"
            });
        }

        var tenant = await _db.WhatsAppTenants
            .FirstOrDefaultAsync(
                x => x.PhoneNumberId == dto.PhoneNumberId, ct);

        if (tenant == null)
        {
            tenant = new WhatsAppTenantEntity
            {
                TenantId = dto.TenantId,
                PhoneNumberId = dto.PhoneNumberId,
                WabaId = dto.WabaId ?? "",
                BusinessTokenEncrypted = dto.AccessToken,
                IsActive = true
            };

            _db.WhatsAppTenants.Add(tenant);
        }
        else
        {
            tenant.TenantId = dto.TenantId;
            tenant.WabaId = dto.WabaId ?? tenant.WabaId;
            tenant.BusinessTokenEncrypted = dto.AccessToken;
            tenant.IsActive = true;
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            ok = true,
            message = "Saved successfully",
            tenantId = dto.TenantId
        });
    }

    // GET: api/whatsapp/status
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        [FromQuery] string tenantId,
        CancellationToken ct)
    {
        var tenant = await _db.WhatsAppTenants
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);

        if (tenant == null)
            return Ok(new { ok = true, connected = false });

        return Ok(new
        {
            ok = true,
            connected = true,
            tenantId = tenant.TenantId,
            phoneNumberId = tenant.PhoneNumberId,
            wabaId = tenant.WabaId
        });
    }

    // GET: api/whatsapp/messages
    [HttpGet("messages")]
    public async Task<IActionResult> GetMessages(
        [FromQuery] string? tenantId,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            tenantId = "default";

        var safeTake = Math.Clamp(take, 1, 200);

        var items = await _db.WhatsAppMessages
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderByDescending(m => m.TimestampUtc)
            .Take(safeTake)
            .Select(m => new
            {
                id = m.Id,
                direction = m.Direction,
                messageType = m.MessageType,
                textPreview = m.TextPreview,
                status = m.Status,
                timestampUtc = m.TimestampUtc,
                wamid = m.Wamid
            })
            .ToListAsync(ct);

        return Ok(new
        {
            ok = true,
            messages = items
        });
    }

    // POST: api/whatsapp/send
    [HttpPost("send")]
    public async Task<IActionResult> SendMessage(
        [FromBody] SendMessageDto dto,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.TenantId)
            || string.IsNullOrWhiteSpace(dto.To)
            || string.IsNullOrWhiteSpace(dto.Text))
        {
            return BadRequest(new
            {
                ok = false,
                message = "TenantId, To and Text are required"
            });
        }

        var account = await _db.WhatsAppTenants
            .FirstOrDefaultAsync(
                x => x.TenantId == dto.TenantId && x.IsActive, ct);

        if (account == null)
        {
            return BadRequest(new
            {
                ok = false,
                message = "WhatsApp account is not connected for this tenant"
            });
        }

        var client = _httpFactory.CreateClient("whatsapp");

        // Meta Graph API version: v26.0
        client.BaseAddress = new Uri(
            "https://graph.facebook.com/v26.0/");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                account.BusinessTokenEncrypted);

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = dto.To,
            type = "text",
            text = new
            {
                preview_url = false,
                body = dto.Text
            }
        };

        var response = await client.PostAsJsonAsync(
            $"{account.PhoneNumberId}/messages",
            payload,
            ct);

        var responseBody =
            await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "WhatsApp send failed: {Status} {Body}",
                response.StatusCode,
                responseBody);

            return BadRequest(new
            {
                ok = false,
                message = "Send failed",
                detail = responseBody
            });
        }

        var conversation = await _db.WhatsAppConversations
            .FirstOrDefaultAsync(c =>
                c.TenantId == dto.TenantId &&
                c.PhoneNumberId == account.PhoneNumberId &&
                c.ContactPhone == dto.To, ct);

        if (conversation == null)
        {
            conversation = new WhatsAppConversationEntity
            {
                TenantId = dto.TenantId,
                PhoneNumberId = account.PhoneNumberId,
                ContactPhone = dto.To,
                Status = "open",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _db.WhatsAppConversations.Add(conversation);
            await _db.SaveChangesAsync(ct);
        }

        _db.WhatsAppMessages.Add(new WhatsAppMessageEntity
        {
            TenantId = dto.TenantId,
            ConversationId = conversation.Id,
            Direction = "Outbound",
            MessageType = "text",
            ContentJson = JsonSerializer.Serialize(payload),
            TextPreview = dto.Text,
            Status = "Sent",
            TimestampUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            ok = true,
            message = "Message sent"
        });
    }

    private static string? GetString(
        JsonElement element,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? GetNestedString(
        JsonElement element,
        string parent,
        string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(parent, out var nested))
            return null;

        return GetString(nested, property);
    }
}

public sealed record SaveConnectionDto(
    string TenantId,
    string PhoneNumberId,
    string AccessToken,
    string? WabaId,
    string? VerifyToken);

public sealed record SendMessageDto(
    string TenantId,
    string To,
    string Text);
