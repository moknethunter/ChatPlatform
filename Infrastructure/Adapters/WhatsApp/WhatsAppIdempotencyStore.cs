using Microsoft.Extensions.Caching.Memory;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppIdempotencyStore
{
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public WhatsAppIdempotencyStore(IMemoryCache cache) => _cache = cache;

    public bool TryRegister(string wamid)
    {
        if (string.IsNullOrWhiteSpace(wamid)) return true;

        var key = $"wamid:{wamid}";
        if (_cache.TryGetValue(key, out _)) return false;

        _cache.Set(key, true, Ttl);
        return true;
    }
}
