using ChatPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppReceiptWorker(IServiceScopeFactory scopes,ILogger<WhatsAppReceiptWorker> log):BackgroundService
{
    // Delivery receipts only advance evidence of success; a late sent/failed receipt
    // must never downgrade a delivered or read message.
    public static string NextStatus(string current,string receipt)=>receipt switch
    {
        "read" when current is "Sent" or "Delivered" or "Failed" or "Read" => "Read",
        "delivered" when current is "Sent" or "Failed" or "Delivered" => "Delivered",
        "failed" when current=="Sent" => "Failed",
        _=>current
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{if(!await ProcessAsync(ct))await Task.Delay(1000,ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex){log.LogError(ex,"Receipt worker failed");await Task.Delay(5000,ct);}
        }
    }

    private async Task<bool> ProcessAsync(CancellationToken ct)
    {
        using var scope=scopes.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        // Match signed, persisted receipt by all ownership dimensions. Unmatched receipts
        // remain available for a send still committing its Meta ID; they do not block matches.
        var candidates=from r in db.WhatsAppStatusInbox.AsNoTracking()
            join m in db.WhatsAppMessages.AsNoTracking() on r.Wamid equals m.Wamid
            join c in db.WhatsAppConversations.AsNoTracking() on m.ConversationId equals c.Id
            where !r.Processed && r.TenantId==m.TenantId && r.TenantId==c.TenantId
                && r.PhoneNumberId==c.PhoneNumberId && r.ContactPhone==c.ContactPhone && m.Direction=="Outbound"
            orderby r.ReceivedAtUtc
            select new{r.Id,MessageId=m.Id,r.TenantId,r.Status};
        var row=await candidates.FirstOrDefaultAsync(ct);
        if(row==null)return false;
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var receipt=await db.WhatsAppStatusInbox.SingleOrDefaultAsync(r=>r.Id==row.Id&&r.TenantId==row.TenantId&&!r.Processed,ct);
        if(receipt==null)return true;
        var message=await db.WhatsAppMessages.SingleOrDefaultAsync(m=>m.Id==row.MessageId&&m.TenantId==row.TenantId&&m.Direction=="Outbound",ct);
        if(message!=null)message.Status=NextStatus(message.Status,row.Status);
        receipt.Processed=true;receipt.ProcessedAtUtc=DateTime.UtcNow;
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;
    }
}
