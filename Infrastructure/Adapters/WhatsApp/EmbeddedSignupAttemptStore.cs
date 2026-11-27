using System.Security.Cryptography;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Single-process, bounded, single-use correlation for the SDK signup attempt.
/// Restart or routing to a different process fails closed; it is not a substitute
/// for validating Meta's authorization code and account ownership.</summary>
public sealed class EmbeddedSignupAttemptStore
{
    private sealed record Attempt(string Tenant, string Session, string AppId, string ConfigId, DateTimeOffset Expires);
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private const int Limit = 2048;

    public string? Start(string tenant, string session, string appId, string configId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(session)) return null;
        lock (_gate)
        {
            foreach (var key in _attempts.Where(x => x.Value.Expires <= now ||
                (x.Value.Tenant == tenant && x.Value.Session == session)).Select(x => x.Key).ToArray())
                _attempts.Remove(key);
            if (_attempts.Count >= Limit) return null;
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _attempts.Add(state, new(tenant, session, appId, configId, now.AddMinutes(5)));
            return state;
        }
    }

    public bool Consume(string? state, string tenant, string session, string appId, string configId, DateTimeOffset now)
    {
        if (state is not { Length: 64 }) return false;
        lock (_gate)
        {
            if (!_attempts.TryGetValue(state, out var attempt)) return false;
            if (attempt.Expires <= now) { _attempts.Remove(state); return false; }
            if (attempt.Tenant != tenant || attempt.Session != session ||
                attempt.AppId != appId || attempt.ConfigId != configId) return false;
            _attempts.Remove(state);
            return true;
        }
    }
}
