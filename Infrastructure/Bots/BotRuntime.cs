using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Bots;

public static class BotRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Called only by the durable, signed webhook inbox path. The caller saves the inbound
    // message, execution receipt and outbound rows together, so a failed commit queues nothing.
    public static async Task StageAsync(PlatformDbContext db, WhatsAppConversationEntity conversation,
        WhatsAppMessageEntity inbound, string? text, DateTime now, CancellationToken ct)
    {
        if (inbound.Direction != "Inbound" || inbound.MessageType != "text" || string.IsNullOrWhiteSpace(text)
            || inbound.TenantId != conversation.TenantId || inbound.ConversationId != conversation.Id
            || string.IsNullOrWhiteSpace(inbound.Wamid) || conversation.ContactPhone.Length is < 7 or > 15
            || conversation.ContactPhone[0] == '0' || conversation.ContactPhone.Any(c => c is < '0' or > '9')) return;
        var tenant = conversation.TenantId;
        if (db.AutomationRuns.Local.Any(x => x.InboundMessageId == inbound.Id)
            || await db.AutomationRuns.AnyAsync(x => x.InboundMessageId == inbound.Id, ct)) return;

        var release = await db.BotReleases.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant, ct);
        var run = new AutomationRunEntity { InboundMessageId = inbound.Id, TenantId = tenant,
            ConversationId = conversation.Id, RecipientPhone = conversation.ContactPhone, CreatedAtUtc = now,
            BotId = release?.DraftId, BotVersion = release?.Version };
        db.AutomationRuns.Add(run);
        if (conversation.LastInboundAtUtc is not DateTime at || at > now || now - at >= TimeSpan.FromHours(24)
            || !await db.WhatsAppTenants.AsNoTracking().AnyAsync(a => a.TenantId == tenant && a.PhoneNumberId == conversation.PhoneNumberId
                && a.IsActive && a.BusinessTokenEncrypted != "", ct)
            || !await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenant && t.IsActive, ct))
        { run.Outcome = "account_or_window_unavailable"; return; }

        string reply;
        int cooldown;
        if (release != null)
        {
            // A published flow takes exclusive priority, even if it produces no reply.
            BotFlow.Definition? flow;
            try { flow = JsonSerializer.Deserialize<BotFlow.Definition>(release.DefinitionJson, Json); }
            catch (JsonException) { run.Outcome = "invalid_release"; return; }
            if (BotFlow.Validate(flow) != null) { run.Outcome = "invalid_release"; return; }
            reply = string.Join("\n\n", BotFlow.Evaluate(flow!, text).Replies);
            cooldown = 5;
        }
        else
        {
            var rules = await db.AutoReplyRules.AsNoTracking().Where(r => r.IsActive &&
                (r.TenantId == tenant || (tenant == TenantEntity.DefaultTenantId && r.TenantId == "default")))
                .OrderBy(r => r.Priority).ThenBy(r => r.Id).Take(100).ToListAsync(ct);
            var matched = rules.FirstOrDefault(r => WhatsAppAutoReplyAdapter.Matches(r, text));
            if (matched == null) { run.Outcome = "no_match"; return; }
            run.RuleId = matched.Id;
            reply = matched.ReplyText.Trim().Replace("{message}", text.Trim(), StringComparison.OrdinalIgnoreCase)
                .Replace("{text}", text.Trim(), StringComparison.OrdinalIgnoreCase);
            cooldown = Math.Clamp(matched.CooldownSeconds, 0, 86400);
        }
        if (string.IsNullOrWhiteSpace(reply)) { run.Outcome = "no_match"; return; }
        if (reply.Length > 4000) reply = reply[..4000];
        var since = now.AddSeconds(-cooldown);
        if (cooldown > 0 && await db.AutomationRuns.AsNoTracking().AnyAsync(r => r.TenantId == tenant &&
            r.RecipientPhone == conversation.ContactPhone && r.Outcome == "queued" && r.CreatedAtUtc >= since, ct))
        { run.Outcome = "cooldown"; return; }

        var key = "automation:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenant+"\n"+inbound.Wamid)));
        if (db.WhatsAppOutbox.Local.Any(x => x.TenantId == tenant && x.IdempotencyKey == key)
            || await db.WhatsAppOutbox.AnyAsync(x => x.TenantId == tenant && x.IdempotencyKey == key, ct))
        { run.Outcome = "duplicate"; return; }
        var payload = JsonSerializer.Serialize(new { messaging_product = "whatsapp", recipient_type = "individual",
            to = conversation.ContactPhone, type = "text", text = new { body = reply, preview_url = false } });
        var outgoing = new WhatsAppMessageEntity { TenantId = tenant, ConversationId = conversation.Id,
            Direction = "Outbound", MessageType = "text", ContentJson = payload, TextPreview = reply.Length > 200 ? reply[..200] : reply,
            Status = "Queued", TimestampUtc = now, CreatedAtUtc = now };
        db.WhatsAppMessages.Add(outgoing);
        db.WhatsAppOutbox.Add(new WhatsAppOutboxEntity { TenantId = tenant, PhoneNumberId = conversation.PhoneNumberId,
            MessageId = outgoing.Id, IdempotencyKey = key, RecipientPhone = conversation.ContactPhone, MessageType = "text",
            PayloadJson = payload, CreatedAtUtc = now, UpdatedAtUtc = now, NextAttemptAtUtc = now, Status = "Pending" });
        run.OutboundMessageId = outgoing.Id; run.Outcome = "queued";
    }
}
