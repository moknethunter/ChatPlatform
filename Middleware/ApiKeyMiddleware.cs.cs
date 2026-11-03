namespace ChatPlatform.Middleware;

public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _validApiKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _validApiKey = configuration["Security:ApiKey"] ?? Environment.GetEnvironmentVariable("CHATPLATFORM_API_KEY");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var requiresApiKey = path.StartsWithSegments("/api/admin") ||
                             path.StartsWithSegments("/api/connectors") ||
                             path.StartsWithSegments("/api/events") ||
                             path.StartsWithSegments("/api/inbound") ||
                             path.StartsWithSegments("/api/whatsapp");
        if (requiresApiKey)
        {
            if (string.IsNullOrWhiteSpace(_validApiKey))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { error = "API key is not configured." });
                return;
            }

            if (!context.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) ||
                !string.Equals(providedKey.ToString(), _validApiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Invalid API key." });
                return;
            }
        }

        await _next(context);
    }
}
