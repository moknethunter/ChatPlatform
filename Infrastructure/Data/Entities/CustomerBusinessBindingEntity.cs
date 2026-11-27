namespace ChatPlatform.Infrastructure.Data.Entities;

// Explicit, short-lived staff attestation for one conversation, source and operation.
// ربط مؤقت معتمد من المسؤول؛ ليس إثبات هوية آليًا.
public sealed class CustomerBusinessBindingEntity
{
    public string TenantId {get;set;}="";
    public Guid ConversationId {get;set;}
    public string Source {get;set;}="";
    public string Operation {get;set;}="";
    public string ProtectedReference {get;set;}="";
    public string VerifiedBy {get;set;}="";
    public string VerificationMethod {get;set;}="";
    public DateTime VerifiedAtUtc {get;set;}
    public DateTime ExpiresAtUtc {get;set;}
    // Changes on every grant/revoke, preventing stale reads from being returned.
    public Guid Revision {get;set;}
    public bool Revoked {get;set;}
}
