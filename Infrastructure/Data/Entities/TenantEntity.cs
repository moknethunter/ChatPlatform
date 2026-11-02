namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class TenantEntity
{
    public const string DefaultTenantId = "00000000-0000-0000-0000-000000000001";

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
