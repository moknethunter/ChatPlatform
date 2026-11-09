namespace ChatPlatform.Infrastructure.Data.Entities;

/// <summary>
/// قاعدة رد آلي — تُطابق رسالة واردة وتُرسل رداً نصياً.
/// MatchType:
///   - "any"         : يطابق كل الرسائل النصية
///   - "exact"       : يطابق النص بالضبط
///   - "contains"    : يطابق إذا احتوى النص على الكلمة
///   - "starts_with" : يبدأ بـ
///   - "regex"       : تعبير نمطي
/// </summary>
public sealed class AutoReplyRuleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = "default";
    public string Name { get; set; } = "";
    public string MatchType { get; set; } = "contains";
    public string? KeywordPattern { get; set; }
    public string ReplyText { get; set; } = "";
    public int Priority { get; set; } = 100;
    public int CooldownSeconds { get; set; } = 60;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
