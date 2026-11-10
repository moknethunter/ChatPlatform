namespace ChatPlatform.Infrastructure.Data.Entities;

public sealed class WhatsAppTemplateEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // المؤسسة المالكة للقالب
    public string TenantId { get; set; } = "";

    // حساب واتساب المرتبط بالقالب
    public string WhatsAppTenantId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Category { get; set; } = "UTILITY";

    public string Language { get; set; } = "ar";

    // نص القالب المحلي
    public string Body { get; set; } = "";

    // القوالب المحلية لا تحتاج اعتماد Meta
    public string Status { get; set; } = "LOCAL";

    // يظل فارغاً حتى يتم ربطه بقالب Meta رسمي
    public string? MetaTemplateId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
