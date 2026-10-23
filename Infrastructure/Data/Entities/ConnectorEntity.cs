namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class ConnectorEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = default!;
    public string TargetUrl { get; set; } = default!;
    public string HttpMethod { get; set; } = "POST";
    public string AuthHeaderKey { get; set; } = "Authorization";
    public string AuthToken { get; set; } = default!;
    public string PayloadTemplate { get; set; } = default!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
