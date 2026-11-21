namespace ChatPlatform.Infrastructure.Data.Entities;
public sealed class MarketingConsentEntity
{
    public string TenantId {get;set;}="";
    public string PhoneNumberId {get;set;}="";
    public string Phone {get;set;}="";
    public bool Granted {get;set;}
    public string Evidence {get;set;}="";
    public string UpdatedBy {get;set;}="";
    public DateTime UpdatedAtUtc {get;set;}
}
public sealed class ConsentEventEntity
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string TenantId {get;set;}="";
    public string PhoneNumberId {get;set;}="";
    public string Phone {get;set;}="";
    public bool Granted {get;set;}
    public string Evidence {get;set;}="";
    public string UpdatedBy {get;set;}="";
    public DateTime UpdatedAtUtc {get;set;}
}
public sealed class CustomerListEntity
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string TenantId {get;set;}="";
    public string Name {get;set;}="";
    public string PhonesJson {get;set;}="[]";
    public int Version {get;set;}=1;
}
public sealed class MarketingCampaignEntity
{
    public Guid Id {get;set;}
    public string TenantId {get;set;}="";
    public string Name {get;set;}="";
    public Guid ListId {get;set;}
    public string TemplateId {get;set;}="";
    public string PhoneNumberId {get;set;}="";
    public string Status {get;set;}="Scheduled";
    public DateTime ScheduledAtUtc {get;set;}
    public DateTime CreatedAtUtc {get;set;}
    public string CreatedBy {get;set;}="";
}
public sealed class CampaignRecipientEntity
{
    public string TenantId {get;set;}="";
    public Guid CampaignId {get;set;}
    public string Phone {get;set;}="";
    public Guid OutboxId {get;set;}
}
