namespace ChatPlatform.Infrastructure.Data.Entities;
public sealed class InboxRevisionEntity
{
    public string TenantId { get; set; } = "";
    public long Revision { get; set; }
}
