namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class AutomationRunEntity
{
    public Guid InboundMessageId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid ConversationId { get; set; }
    public string RecipientPhone { get; set; } = "";
    public Guid? BotId { get; set; }
    public int? BotVersion { get; set; }
    public Guid? RuleId { get; set; }
    public string Outcome { get; set; } = "";
    public Guid? OutboundMessageId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
