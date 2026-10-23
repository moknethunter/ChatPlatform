using System.Text;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters;

public sealed class UniversalConnectorAdapter : IIntegrationAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UniversalConnectorAdapter> _log;

    public UniversalConnectorAdapter(IHttpClientFactory httpClientFactory, IServiceScopeFactory scopeFactory, ILogger<UniversalConnectorAdapter> log)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public string Name => "UniversalConnector";

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (evt.Type != "message.created" || evt.Source != "chat-app") return;

        var messageText = evt.Payload.TryGetProperty("Text", out var t) ? t.GetString() : "";
        var recipient = evt.Payload.TryGetProperty("Recipient", out var r) ? r.GetString() : "";

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        
        var connectors = await db.Connectors.AsNoTracking().ToListAsync(ct);
        if (connectors.Count == 0) return;

        // استخدام العميل المحمي من الانهيار
        var client = _httpClientFactory.CreateClient("ResilientClient");

        foreach (var connector in connectors)
        {
            try
            {
                var payload = connector.PayloadTemplate.Replace("{Message}", messageText).Replace("{Recipient}", recipient);
                var request = new HttpRequestMessage(new HttpMethod(connector.HttpMethod), connector.TargetUrl);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                if (!string.IsNullOrEmpty(connector.AuthToken))
                {
                    var tokenValue = connector.AuthHeaderKey.Equals("Authorization", StringComparison.OrdinalIgnoreCase) 
                                     && !connector.AuthToken.StartsWith("Bearer") ? $"Bearer {connector.AuthToken}" : connector.AuthToken;
                    request.Headers.TryAddWithoutValidation(connector.AuthHeaderKey, tokenValue);
                }

                var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                    _log.LogInformation("✅ Delivered to {Name}", connector.Name);
                else
                    _log.LogWarning("⚠️ Failed to deliver to {Name}. Status: {Status}", connector.Name, response.StatusCode);
            }
            catch (Exception ex) 
            { 
                _log.LogError(ex, "Error delivering to {Name}", connector.Name); 
            }
        }
    }
}
