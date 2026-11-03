using System.Security.Cryptography;
using System.Text;

namespace ChatPlatform.Middleware;

/// <summary>Protects private API routes without shipping a usable default key.</summary>
public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _configuredApiKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuredApiKey = configuration["Security:ApiKey"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresApiKey(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_configuredApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "API key authentication is not configured."
            });
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var suppliedValues) ||
            suppliedValues.Count != 1 ||
            !KeysMatch(suppliedValues[0], _configuredApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Unauthorized. Invalid API key."
            });
            return;
        }

        await _next(context);
    }

    private static bool RequiresApiKey(PathString path)
        => path.StartsWithSegments("/api/admin") ||
           path.StartsWithSegments("/api/connectors") ||
           path.StartsWithSegments("/api/events") ||
           path.StartsWithSegments("/api/whatsapp");

    private static bool KeysMatch(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied))
            return false;

        // Hash to a fixed-size value before constant-time comparison.
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }
}
