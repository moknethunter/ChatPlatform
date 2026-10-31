using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters;

// ═══════════════════════════════════════════════════════════════════════════
//  WhatsAppAdapter — يخدم الإرسال والاستقبال عبر Cloud API
// ═══════════════════════════════════════════════════════════════════════════
public class WhatsAppAdapter : IIntegrationAdapter
{
    public const string InboundType = "whatsapp.inbound";
    public const string OutboundType = "whatsapp.send";

    public string Name => "whatsapp";

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<WhatsAppAdapter> _log;

    public WhatsAppAdapter(IHttpClientFactory http, IConfiguration cfg, ILogger<WhatsAppAdapter> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        // 1) تجاهل إعلانات المنصة
        if (evt.Source?.StartsWith("platform.adapters", StringComparison.OrdinalIgnoreCase) == true)
            return;

        // 2) استقبال رسالة واردة ← وإرسال رد تلقائي
        if (string.Equals(evt.Type, InboundType, StringComparison.OrdinalIgnoreCase))
        {
            var from = Get(evt, "from");
            var text = Get(evt, "text");
            if (from is null || text is null) return;

            await SendMetaMessageAsync(from, $"أهلاً بك! وصلتني رسالتك: «{text}»", ct);
            return;
        }

        // 3) إرسال رسالة صادرة (عبر /api/events)
        if (string.Equals(evt.Type, OutboundType, StringComparison.OrdinalIgnoreCase))
        {
            var to = Get(evt, "to");
            var text = Get(evt, "text");
            if (to is null || text is null) return;

            await SendMetaMessageAsync(to, text, ct);
        }
    }

    private static string? Get(PlatformEvent evt, string key)
        => evt.Payload.TryGetProperty(key, out var v) ? v.GetString() : null;

    private async Task SendMetaMessageAsync(string to, string text, CancellationToken ct)
    {
        var phoneId = _cfg["WhatsApp:PhoneId"];
        var token = _cfg["WhatsApp:Token"];

        if (string.IsNullOrEmpty(phoneId) || string.IsNullOrEmpty(token))
        {
            _log.LogWarning("WhatsApp configuration missing (PhoneId or Token).");
            return;
        }

        var client = _http.CreateClient("whatsapp");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{phoneId}/messages")
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                to = to,
                type = "text",
                text = new { body = text }
            })
        };
        req.Headers.Authorization = new("Bearer", token);

        try
        {
            var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
                _log.LogInformation("WhatsApp sent to {To}", to);
            else
                _log.LogWarning("WhatsApp send failed: {Body}", body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WhatsApp send exception");
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  ربط الـ Adapter بالمنصة + الـ Webhook
// ═══════════════════════════════════════════════════════════════════════════
public static class WhatsAppExtensions
{
    public static IServiceCollection AddWhatsAppAdapter(this IServiceCollection s)
    {
        s.AddHttpClient("whatsapp", c => c.BaseAddress = new Uri("https://graph.facebook.com/v21.0/"));
        s.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();
        return s;
    }

    public static IEndpointRouteBuilder MapWhatsAppWebhook(this IEndpointRouteBuilder app, IConfiguration cfg)
    {
        var verifyToken = cfg["WhatsApp:VerifyToken"];

        // 1) التحقق (Meta GET)
        app.MapGet("/webhook", (HttpContext ctx) =>
        {
            var mode = ctx.Request.Query["hub.mode"];
            var token = ctx.Request.Query["hub.verify_token"];
            var challenge = ctx.Request.Query["hub.challenge"];

            if (mode == "subscribe" && token == verifyToken)
                return Results.Text(challenge.ToString(), "text/plain");

            return Results.Unauthorized();
        });

        // 2) الاستقبال (Meta POST)
        app.MapPost("/webhook", async (HttpContext ctx, IEventBus bus, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("WhatsApp.Webhook");
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                var root = doc.RootElement;

                if (!root.TryGetProperty("entry", out var entries)) return Results.Ok();

                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes)) continue;
                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value)) continue;
                        if (!value.TryGetProperty("messages", out var msgs)) continue;

                        foreach (var m in msgs.EnumerateArray())
                        {
                            if (!m.TryGetProperty("text", out var t) || !t.TryGetProperty("body", out var body)) continue;

                            var from = m.GetProperty("from").GetString();
                            var text = body.GetString();

                            // 🎯 النشر على ناقل الأحداث (CoreEngine سيوزعها على كل الـ Adapters)
                            await bus.PublishAsync(new PlatformEvent
                            {
                                Type = WhatsAppAdapter.InboundType,
                                Source = "whatsapp",
                                Payload = JsonSerializer.SerializeToElement(new { from, text })
                            }, ct);

                            log.LogInformation("WhatsApp inbound from {From}: {Text}", from, text);
                        }
                    }
                }
                return Results.Ok();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Webhook parse failed");
                return Results.Ok(); // نرجع Ok دائماً لـ Meta حتى لا تعيد المحاولة
            }
        });

        return app;
    }
}
