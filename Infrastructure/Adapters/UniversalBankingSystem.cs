using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Hubs;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Infrastructure.Engine;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace ChatPlatform.Infrastructure.Adapters
{
    public class BankConfig
    {
        public string BankName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public BankParser Parser { get; set; } = new();
    }
    public class BankParser
    {
        public string AmountPattern { get; set; } = @"تحويل\s*([\d,]+\.\d+)";
        public string NamePattern { get; set; } = @"لحساب\s+(.+?)(?:\s*\d|رصيدك|YER)";
        public string BalancePattern { get; set; } = @"رصيدك(?: الآن)?\s*([\d,]+\.\d+)";
    }

    public static class BankRegistry
    {
        public static Dictionary<string, BankConfig> Banks { get; } = new();
        public static void RegisterBank(BankConfig c) => Banks[c.BankName] = c;
    }

    public class UniversalBankAdapter : IIntegrationAdapter
    {
        private readonly IEventBus _bus;
        private readonly PlatformDbContext _db;
        public UniversalBankAdapter(IEventBus bus, PlatformDbContext db) { _bus = bus; _db = db; }
        public bool CanHandle(string platform) => BankRegistry.Banks.ContainsKey(platform);
        public async Task HandleAsync(PlatformEvent evt)
        {
            // يستخدم نفس فروع منصتك
            var entity = new ConnectorEntity
            {
                Id = Guid.NewGuid().ToString(),
                Platform = evt.Source,
                RawPayload = evt.Payload.ToString()
            };
            _db.Connectors.Add(entity);
            await _db.SaveChangesAsync();
            await _bus.PublishAsync(new PlatformEvent { Type = "bank.receipt.sent", Source = "universal", Payload = JsonDocument.Parse("{}").RootElement });
        }
    }

    public class WhatsAppVerificationAdapter : IIntegrationAdapter
    {
        private readonly PlatformDbContext _db;
        public WhatsAppVerificationAdapter(PlatformDbContext db) { _db = db; }
        public bool CanHandle(string platform) => platform == "whatsapp_verifier";
        public Task HandleAsync(PlatformEvent evt) => Task.CompletedTask;

        public class WhatsAppCheckResult
        {
            public bool HasWhatsApp { get; set; }
            public bool IsDelivered { get; set; }
            public bool IsRead { get; set; }
            public string Status { get; set; } = "";
        }
        public async Task<WhatsAppCheckResult> CheckNumberHasWhatsAppAsync(string phone)
        {
            return new WhatsAppCheckResult { HasWhatsApp = true, Status = "exists" };
        }
    }

    public class BankResult
    {
        public bool Success { get; set; }
    }
}
