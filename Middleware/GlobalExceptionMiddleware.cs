using System.Net;
using System.Text.Json;

namespace ChatPlatform.Middleware;

public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _log;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled exception occurred: {Message}", ex.Message);
            
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            var response = new { error = "حدث خطأ داخلي في الخادم. يرجى المحاولة لاحقاً.", traceId = context.TraceIdentifier };
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
        }
    }
}
