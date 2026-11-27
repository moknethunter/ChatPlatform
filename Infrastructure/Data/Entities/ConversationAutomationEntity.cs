namespace ChatPlatform.Infrastructure.Data.Entities;
public sealed class ConversationAutomationEntity
{
    public Guid ConversationId { get; set; }
    public string TenantId { get; set; } = "";
    public string Mode { get; set; } = "bot";
    public Guid? ActivationId { get; set; }
    public string? NextNode { get; set; }
    // Pending business lookup metadata only; never stores financial results / طلب استعلام فقط.
    public string? BusinessSource { get; set; }
    public string? BusinessOperation { get; set; }
    public string VariablesJson { get; set; } = "{}";
    public DateTime? LastInputAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = "";
}
