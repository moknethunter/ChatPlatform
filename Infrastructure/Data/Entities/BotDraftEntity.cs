namespace ChatPlatform.Infrastructure.Data.Entities;
public sealed class BotDraftEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string DefinitionJson { get; set; } = "";
    public int Version { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; }
}
