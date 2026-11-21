using System.Security.Cryptography;
using System.Text;

namespace ChatPlatform.Middleware;

/// <summary>Protects legacy administrative routes with an explicitly configured key.</summary>
// This is not tenant authentication. Do not activate it as a substitute for RBAC.
public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _validApiKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _validApiKey = config["Security:ApiKey"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/admin") ||
            context.Request.Path.StartsWithSegments("/api/connectors"))
        {
            // Fail closed when no administrator key has been configured.
            if (string.IsNullOrWhiteSpace(_validApiKey))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            if (!context.Request.Headers.TryGetValue("X-Api-Key", out var supplied) ||
                supplied.Count != 1 ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(supplied.ToString()),
                    Encoding.UTF8.GetBytes(_validApiKey)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        await _next(context);
    }
}
