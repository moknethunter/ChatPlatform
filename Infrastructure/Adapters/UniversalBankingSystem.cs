using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;

namespace ChatPlatform.Infrastructure.Adapters;

public class BankConfig { public string BankName { get; set; } = ""; public string ApiUrl { get; set; } = ""; public bool Enabled { get; set; } = true; }

public static class BankRegistry { public static Dictionary<string, BankConfig> Banks { get; } = new(); public static void RegisterBank(BankConfig c) => Banks[c.BankName] = c; }

public class UniversalBankAdapter : IIntegrationAdapter
{
    public string Name => "universal_bank";
    private readonly PlatformDbContext _db;
    public UniversalBankAdapter(PlatformDbContext db) => _db = db;
    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        _db.Connectors.Add(new ConnectorEntity { Id = Guid.NewGuid().ToString(), Platform = evt.Source, RawPayload = evt.Payload.ToString() });
        await _db.SaveChangesAsync(ct);
    }
}

public class WhatsAppVerificationAdapter : IIntegrationAdapter
{
    public string Name => "whatsapp_verifier";
    public Task HandleEventAsync(PlatformEvent evt, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> HasWhatsAppAsync(string phone) => Task.FromResult(true);
    public Task<bool> IsDeliveredAsync(string id) => Task.FromResult(true);
}
