namespace ChatPlatform.Infrastructure.Configuration;

public static class DynamicSettings
{
    public static string ServerUrl { get; set; } = "http://localhost:5000";
    public static string WhatsappPhoneId { get; set; } = "";
    public static string WhatsappToken { get; set; } = "";
    
    // لتسجيل الأرقام ديناميكياً
    public static Dictionary<string, string> RegisteredNumbers { get; } = new();
}
