namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class CrmContactEntity
{
    public string TenantId { get; set; } = "";
    public string Phone { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Stage { get; set; } = "new";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
