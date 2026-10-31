using System.Net.Http.Json;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;

namespace ChatPlatform.Infrastructure.Adapters;

public class WhatsAppAdapter : IIntegrationAdapter
{
    public const string InboundType  = "whatsapp.inbound";
    public const string OutboundType = "whatsapp.send";

    public string Name => "whatsapp";

    private readonly IHttpClientFactory      _http;
    private readonly IConfiguration          _cfg;
    private readonly ILogger<WhatsAppAdapter> _log;

    public WhatsAppAdapter(IHttpClientFactory http, IConfiguration cfg, ILogger<WhatsAppAdapter> log)
    {
        _http = http;
        _cfg  = cfg;
        _log  = log;
    }

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        // (1) وارد → رد تلقائي عبر نفس القناة (يثبت أن المنصة كاملة تعمل)
        if (string.Equals(evt.Type, InboundType, StringComparison.OrdinalIgnoreCase))
        {
            if (evt.Payload.TryGetProperty("from", out var f) &&
                evt.Payload.TryGetProperty("text", out var t))
                await SendAsync(f.GetString()!, $"👋 وصلني: «{t.GetString()}»", ct);
            return;
        }

        // (2) صادر → يُرسَل إلى واتساب (يُطلَق عبر POST /api/events)
        if (string.Equals(evt.Type, OutboundType, StringComparison.OrdinalIgnoreCase))
        {
            if (evt.Payload.TryGetProperty("to", out var to) &&
                evt.Payload.TryGetProperty("text", out var txt))
                await SendAsync(to.GetString()!, txt.GetString()!, ct);
        }
    }

    private async Task SendAsync(string to, string text, CancellationToken ct)
    {
        var phoneId = _cfg["WhatsApp:PhoneNumberId"];
        var token   = _cfg["WhatsApp:AccessToken"];
        if (string.IsNullOrEmpty(phoneId) || string.IsNullOrEmpty(token))
        {
            _log.LogWarning("WhatsApp:PhoneNumberId/AccessToken غير مضبوطة");
            return;
        }

        var client = _http.CreateClient("whatsapp");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{phoneId}/messages")
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { body = text }
            })
        };
        req.Headers.Authorization = new("Bearer", token);

        var res = await client.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            _log.LogError("WhatsApp send failed {Status}: {Body}",
                res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        else
            _log.LogInformation("WhatsApp → {To}: {Text}", to, text);
    }
}

// ---- سطران في Program.cs، لكن هنا مجمّعة للراحة ----
public static class WhatsAppAdapterExtensions
{
    public static IServiceCollection AddWhatsAppAdapter(this IServiceCollection s)
    {
        s.AddHttpClient("whatsapp", c =>
            c.BaseAddress = new Uri("https://graph.facebook.com/v20.0/"));
        s.AddSingleton<IIntegrationAdapter, WhatsAppAdapter>();
        return s;
    }

    public static IEndpointRouteBuilder MapWhatsAppWebhook(
        this IEndpointRouteBuilder app, IConfiguration cfg)
    {
        var verify = cfg["WhatsApp:VerifyToken"] ?? "";

        // مصادقة Meta
        app.MapGet("/api/inbound/whatsapp", (HttpContext ctx) =>
        {
            if (ctx.Request.Query["hub.mode"] == "subscribe" &&
                ctx.Request.Query["hub.verify_token"] == verify)
                return Results.Text(ctx.Request.Query["hub.challenge"].ToString(), "text/plain");
            return Results.Unauthorized();
        });

        // استقبال الرسائل
        app.MapPost("/api/inbound/whatsapp", async (
            HttpContext ctx, IEventBus bus, IConfiguration cfg,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("WhatsApp.Webhook");
            var phoneId = cfg["WhatsApp:PhoneNumberId"];
            if (string.IsNullOrEmpty(phoneId)) return Results.Ok();

            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("entry", out var entries))
                    return Results.Ok();

                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes)) continue;
                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value)) continue;
                        if (!value.TryGetProperty("metadata", out var meta)) continue;
                        if (meta.GetProperty("phone_number_id").GetString() != phoneId) continue;
                        if (!value.TryGetProperty("messages", out var msgs)) continue;

                        foreach (var m in msgs.EnumerateArray())
                        {
                            if (!m.TryGetProperty("text", out var t) ||
                                !t.TryGetProperty("body", out var body)) continue;

                            var from = m.GetProperty("from").GetString();
                            var text = body.GetString();

                            // 🎯 هنا يدخل الحدث إلى CoreEngine ويُوزَّع على كل الـ adapters
                            await bus.PublishAsync(new PlatformEvent
                            {
                                Type   = WhatsAppAdapter.InboundType,
                                Source = "whatsapp",
                                Payload = JsonSerializer.SerializeToElement(new
                                {
                                    from, text, phoneNumberId = phoneId
                                })
                            }, ct);

                            log.LogInformation("WhatsApp inbound from {From}: {Text}", from, text);
                        }
                    }
                }
                return Results.Ok();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "WhatsApp webhook parse failed");
                return Results.Ok(); // Meta تعيد المحاولة عند 5xx
            }
        });

        return app;
    }
}
