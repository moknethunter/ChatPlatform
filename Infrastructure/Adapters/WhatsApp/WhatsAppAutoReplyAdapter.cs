using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppAutoReplyAdapter : IIntegrationAdapter
{
    public string Name => "whatsapp-autoreply";

    private const int MaxReplyLength = 4000;
    private static readonly ConcurrentDictionary<string, DateTime> _lastReplyAt = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly ILogger<WhatsAppAutoReplyAdapter> _log;

    public WhatsAppAutoReplyAdapter(
        IServiceScopeFactory scopeFactory,
        IEventBus bus,
        ILogger<WhatsAppAutoReplyAdapter> log)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _log = log;
    }

    public async Task HandleEventAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (!string.Equals(evt.Type, "whatsapp.inbound", StringComparison.OrdinalIgnoreCase))
            return;

        var from = GetString(evt.Payload, "from");
        var text = GetString(evt.Payload, "text");
        var convIdStr = GetString(evt.Payload, "conversationId");
        var wamid = GetString(evt.Payload, "wamid");

        if (string.IsNullOrWhiteSpace(from) ||
            string.IsNullOrWhiteSpace(text) ||
            !Guid.TryParse(convIdStr, out var convId))
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            var conv = await db.WhatsAppConversations
                .AsNoTracking()
                .Where(c => c.Id == convId)
                .Select(c => new { c.PhoneNumberId, c.TenantId })
                .FirstOrDefaultAsync(ct);

            if (conv == null) return;

            var rules = await db.AutoReplyRules
                .AsNoTracking()
                .Where(r => r.TenantId == conv.TenantId && r.IsActive)
                .OrderBy(r => r.Priority)
                .ToListAsync(ct);

            if (rules.Count == 0) return;

            var matched = rules.FirstOrDefault(r => Matches(r, text));
            if (matched == null) return;

            var rateKey = $"{conv.TenantId}:{from}";
            if (_lastReplyAt.TryGetValue(rateKey, out var lastAt))
            {
                var elapsed = (DateTime.UtcNow - lastAt).TotalSeconds;
                if (elapsed < matched.CooldownSeconds)
                {
                    _log.LogInformation(
                        "[AutoReply] Skipped (cooldown {Elapsed:F0}s < {Cooldown}s) for {From}",
                        elapsed, matched.CooldownSeconds, from);
                    return;
                }
            }

            var reply = matched.ReplyText.Trim()
                .Replace("{message}", text.Trim(), StringComparison.OrdinalIgnoreCase)
                .Replace("{text}", text.Trim(), StringComparison.OrdinalIgnoreCase);

            if (reply.Length > MaxReplyLength)
                reply = reply[..MaxReplyLength];

            await _bus.PublishAsync(new PlatformEvent
            {
                Type = "whatsapp.send",
                Source = "autoreply",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    phoneNumberId = conv.PhoneNumberId,
                    to = from,
                    text = reply
                })
            }, ct);

            _lastReplyAt[rateKey] = DateTime.UtcNow;

            _log.LogInformation(
                "[AutoReply] ✅ Rule '{Rule}' → {From} (wamid={Wamid})",
                matched.Name, from, wamid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AutoReply] Failed to process inbound event");
        }
    }

    private static bool Matches(AutoReplyRuleEntity rule, string text)
    {
        var normalized = text.Trim();
        var pattern = rule.KeywordPattern;

        return rule.MatchType.ToLowerInvariant() switch
        {
            "any" => string.IsNullOrWhiteSpace(pattern),
            "exact" when !string.IsNullOrWhiteSpace(pattern) =>
                string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase),
            "contains" when !string.IsNullOrWhiteSpace(pattern) =>
                normalized.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            "starts_with" when !string.IsNullOrWhiteSpace(pattern) =>
                normalized.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            "regex" when !string.IsNullOrWhiteSpace(pattern) =>
                SafeRegexMatch(normalized, pattern),
            _ => false
        };
    }

    private static bool SafeRegexMatch(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
