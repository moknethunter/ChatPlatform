namespace ChatPlatform.Middleware;

public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/admin") ||
            context.Request.Path.StartsWithSegments("/api/connectors"))
        {
            var expected = Environment.GetEnvironmentVariable("CHATPLATFORM_API_KEY") ?? _configuration["Security:ApiKey"];
            if (string.IsNullOrWhiteSpace(expected))
            {
                _logger.LogError("Management API key is not configured; rejecting management request.");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { error = "Management API is not configured." });
                return;
            }

            var supplied = context.Request.Headers["X-Api-Key"].ToString();
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
            var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
            if (suppliedBytes.Length != expectedBytes.Length ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
                return;
            }
        }

        await _next(context);
    }
}
