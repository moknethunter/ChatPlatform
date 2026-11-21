namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class BotReleaseEntity
{
    public string TenantId { get; set; } = "";
    public Guid DraftId { get; set; }
    public Guid ActivationId { get; set; }
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string DefinitionJson { get; set; } = "";
    public DateTime PublishedAtUtc { get; set; }
}
