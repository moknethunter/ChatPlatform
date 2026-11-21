namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class PlatformSessionEntity
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string TenantId { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}
