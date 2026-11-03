using System.Collections.Concurrent;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Process-local duplicate suppression. Durable deduplication belongs in the persistent inbox.</summary>
public sealed class WhatsAppIdempotencyStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public bool TryRegister(string wamid)
    {
        if (string.IsNullOrWhiteSpace(wamid)) return true;

        var now = DateTimeOffset.UtcNow;
        if (_seen.TryGetValue(wamid, out var expiresAt) && expiresAt > now)
            return false;

        _seen[wamid] = now.Add(Ttl);
        if (_seen.Count > 10_000)
        {
            foreach (var item in _seen)
                if (item.Value <= now) _seen.TryRemove(item.Key, out _);
        }
        return true;
    }
}
