namespace ChatPlatform.Infrastructure.Data.Entities;
public sealed class KnowledgeArticleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsPublished { get; set; }
    public int Version { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; }
}
