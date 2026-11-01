using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppExtensions
{
    public static IServiceCollection AddWhatsAppAdapter(
        this IServiceCollection s, IConfiguration config)
    {
        s.Configure<WhatsAppOptions>(config.GetSection(WhatsAppOptions.SectionName));

        s.AddHttpClient("whatsapp", c =>
        {
            c.BaseAddress = new Uri("https://graph.facebook.com/");
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        s.AddMemoryCache();
        s.AddSingleton<WhatsAppIdempotencyStore>();
        s.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();
        return s;
    }

    public static IEndpointRouteBuilder MapWhatsAppWebhook(
        this IEndpointRouteBuilder app, IConfiguration cfg)
    {
        var verifyToken = cfg["WhatsApp:VerifyToken"] ?? string.Empty;
        var appSecret = cfg["WhatsApp:AppSecret"] ?? string.Empty;

        app.MapGet("/webhook", (HttpContext ctx) =>
        {
            var mode = ctx.Request.Query["hub.mode"];
            var token = ctx.Request.Query["hub.verify_token"];
            var challenge = ctx.Request.Query["hub.challenge"];

            if (mode == "subscribe" && token == verifyToken)
                return Results.Text(challenge.ToString() ?? string.Empty, "text/plain");

            return Results.Unauthorized();
        });

        app.MapPost("/webhook", async (
            HttpContext ctx,
            IEventBus bus,
            WhatsAppIdempotencyStore idem,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("WhatsApp.Webhook");

            try
            {
                ctx.Request.EnableBuffering();

                string payload;
                using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true))
                {
                    payload = await reader.ReadToEndAsync(ct);
                }
                ctx.Request.Body.Position = 0;

                if (!string.IsNullOrWhiteSpace(appSecret))
                {
                    var sig = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
                    if (!WhatsAppSignatureValidator.Validate(payload, sig, appSecret))
                    {
                        log.LogWarning("Invalid WhatsApp signature");
                        return Results.Unauthorized();
                    }
                }

                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("entry", out var entries))
                    return Results.Ok();

                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes)) continue;

                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value)) continue;
                        if (!value.TryGetProperty("messages", out var msgs)) continue;

                        foreach (var m in msgs.EnumerateArray())
                        {
                            if (!m.TryGetProperty("from", out var fromEl)) continue;
                            var from = fromEl.GetString();
                            if (string.IsNullOrWhiteSpace(from)) continue;

                            string? text = null;
                            if (m.TryGetProperty("text", out var t) &&
                                t.TryGetProperty("body", out var b))
                                text = b.GetString();

                            var wamid = m.TryGetProperty("id", out var idEl)
                                ? idEl.GetString()
                                : null;

                            if (!idem.TryRegister(wamid ?? string.Empty))
                            {
                                log.LogInformation("Duplicate wamid skipped: {Id}", wamid);
                                continue;
                            }

                            await bus.PublishAsync(new PlatformEvent
                            {
                                Type = WhatsAppAdapter.InboundType,
                                Source = "whatsapp",
                                Payload = JsonSerializer.SerializeToElement(new
                                {
                                    from,
                                    text,
                                    wamid
                                })
                            }, ct);

                            log.LogInformation("WhatsApp inbound from {From}", from);
                        }
                    }
                }

                return Results.Ok();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Webhook parse failed");
                return Results.Ok();
            }
        });

        return app;
    }
}
