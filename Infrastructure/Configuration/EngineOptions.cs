namespace ChatPlatform.Infrastructure.Configuration;

public sealed class EngineOptions
{
    public int WorkerCount { get; set; } = 8;
    public int QueueCapacity { get; set; } = 5000;
    public int DrainTimeoutSeconds { get; set; } = 15;
    
    // إعدادات إعادة المحاولة
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryBaseDelayMilliseconds { get; set; } = 500;
    public int MaxRetryDelayMilliseconds { get; set; } = 5000;
    
    // إعدادات المراقبة
    public bool EnableMetrics { get; set; } = true;
    public int SlowAdapterThresholdMilliseconds { get; set; } = 2000; 
}
