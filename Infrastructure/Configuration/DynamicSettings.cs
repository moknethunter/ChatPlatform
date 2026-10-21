namespace ChatPlatform.Infrastructure.Configuration;

public static class DynamicSettings
{
    public static string ServerUrl { get; set; } = "http://localhost:5000";
    
    // قائمة التكاملات الديناميكية
    public static List<ConnectorConfig> Connectors { get; } = new();
}
