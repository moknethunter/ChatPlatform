namespace ChatPlatform.Infrastructure.Data.Entities;

public class WhatsAppTenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string WabaId { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    public string BusinessTokenEncrypted { get; set; } = ""; // encrypted — never store the clear token
    public string AppId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public string TenantId { get; set; } = TenantEntity.DefaultTenantId;
    public TenantEntity? Tenant { get; set; }
}
