using System.Security.Cryptography;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Builds stable idempotency keys for Meta webhook events.</summary>
public static class WhatsAppEventKeys
{
    public static string ForMessage(string wamid) => $"msg:{wamid}";

    public static string ForStatus(string wamid, string status, long unixTimestamp)
        => $"status:{wamid}:{status}:{unixTimestamp}";

    public static string ForAccountUpdate(string wabaId, string eventType, long unixTimestamp)
        => $"account:{wabaId}:{eventType}:{unixTimestamp}";

    public static string ForPayloadHash(byte[] payload)
        => $"hash:{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}";
}
