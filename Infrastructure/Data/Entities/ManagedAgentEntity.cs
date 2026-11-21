namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class ManagedAgentEntity
{
    public string Username { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
