namespace ChatPlatform.Infrastructure.Data.Entities;

// One short-lived, staff-attested grant per tenant/conversation.
// تفويض مؤقت لاستعلام واحد بعد تحقق الموظف، وليس إثبات هوية آلي.
public sealed class BusinessVerificationGrantEntity
{
    public string TenantId { get; set; } = "";
    public Guid ConversationId { get; set; }
    public Guid GrantId { get; set; }
    public string AdminUsername { get; set; } = "";
    public string Source { get; set; } = "";
    public string Operation { get; set; } = "";
    public string ProtectedReference { get; set; } = "";
    public DateTime RequestUpdatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
