namespace ChatPlatform.Middleware;

public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _validApiKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _validApiKey = config["Security:ApiKey"] ?? "MySuperSecretKey_12345";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // نحمي مسارات الإدارة والربط، ونترك الواجهة والـ Webhooks مفتوحة
        if (context.Request.Path.StartsWithSegments("/api/admin") || 
            context.Request.Path.StartsWithSegments("/api/connectors"))
        {
            if (!context.Request.Headers.TryGetValue("X-Api-Key", out var extractedApiKey) || extractedApiKey != _validApiKey)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Invalid API Key." });
                return;
            }
        }
        await _next(context);
    }
}
