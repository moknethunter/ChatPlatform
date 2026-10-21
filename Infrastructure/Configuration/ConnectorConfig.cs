namespace ChatPlatform.Infrastructure.Configuration;

public sealed class ConnectorConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = ""; // اسم التكامل (مثلاً: WhatsApp Sales)
    public string TargetUrl { get; set; } = ""; // رابط الـ API
    public string HttpMethod { get; set; } = "POST"; // POST أو GET
    public string AuthHeaderKey { get; set; } = "Authorization"; // اسم الهيدر (مثلاً Authorization أو X-Api-Key)
    public string AuthToken { get; set; } = ""; // التوكن أو المفتاح
    public string PayloadTemplate { get; set; } = "{\"text\":\"{Message}\"}"; // قالب البيانات
}
