using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
namespace ChatPlatform.Infrastructure.Bots;

public static class HumanHandoff
{
    // Caller holds the same SQLite write transaction used for inbox processing.
    public static async Task CancelPendingAsync(PlatformDbContext db, WhatsAppConversationEntity conversation, DateTime now, CancellationToken ct)
    {
        var ids = db.WhatsAppMessages.Where(m => m.TenantId == conversation.TenantId && m.ConversationId == conversation.Id).Select(m => m.Id);
        var pending = db.WhatsAppOutbox.Where(o => o.TenantId == conversation.TenantId && o.MessageId.HasValue &&
            ids.Contains(o.MessageId.Value) && o.Status == "Pending" && o.IdempotencyKey.StartsWith("automation:"));
        var cancelled = await pending.Select(o => o.MessageId!.Value).ToListAsync(ct);
        await pending.ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "Cancelled")
            .SetProperty(o => o.LastError, "human_handoff").SetProperty(o => o.UpdatedAtUtc, now), ct);
        if(cancelled.Count>0) await db.WhatsAppMessages.Where(m => m.TenantId == conversation.TenantId &&
            m.ConversationId == conversation.Id && cancelled.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "Cancelled"), ct);
    }
}
