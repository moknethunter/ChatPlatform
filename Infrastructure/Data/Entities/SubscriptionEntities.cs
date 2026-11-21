namespace ChatPlatform.Infrastructure.Data.Entities;
public sealed class ManagedTenantAdminEntity
{
    public string Username {get;set;}="";
    public string TenantId {get;set;}="";
    public string PasswordHash {get;set;}="";
    public bool IsActive {get;set;}=true;
    public DateTime CreatedAtUtc {get;set;}=DateTime.UtcNow;
}
public sealed class ServicePlanEntity
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Name {get;set;}="";
    public int MonthlyMessages {get;set;}
    public int Agents {get;set;}
    public int Accounts {get;set;}
    public int Bots {get;set;}
    public int Articles {get;set;}
    public bool Campaigns {get;set;}
    public bool OwnModel {get;set;}
    public long MonthlyPriceMinor {get;set;}
    public string Currency {get;set;}="USD";
    public DateTime CreatedAtUtc {get;set;}=DateTime.UtcNow;
}
public sealed class TenantSubscriptionEntity
{
    public string TenantId {get;set;}="";
    public Guid PlanId {get;set;}
    public bool Enabled {get;set;}=true;
    public DateTime EndsAtUtc {get;set;}
    public int Version {get;set;}=1;
    public DateTime UpdatedAtUtc {get;set;}=DateTime.UtcNow;
}
public sealed class PlatformAdminAuditEntity
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Actor {get;set;}="";
    public string Action {get;set;}="";
    public string Target {get;set;}="";
    public string Detail {get;set;}="";
    public DateTime CreatedAtUtc {get;set;}=DateTime.UtcNow;
}
