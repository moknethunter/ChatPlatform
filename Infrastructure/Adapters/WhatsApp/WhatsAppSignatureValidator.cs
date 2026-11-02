using System.Security.Cryptography;
using System.Text;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Validates Meta's X-Hub-Signature-256 against the exact HTTP request bytes.</summary>
public static class WhatsAppSignatureValidator
{
    public static bool Validate(ReadOnlySpan<byte> payload, string? signature, string? appSecret)
    {
        if (string.IsNullOrWhiteSpace(appSecret) || string.IsNullOrWhiteSpace(signature))
            return false;

        const string prefix = "sha256=";
        if (!signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signature[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (supplied.Length != SHA256.HashSizeInBytes)
            return false;

        var key = Encoding.UTF8.GetBytes(appSecret);
        var expected = HMACSHA256.HashData(key, payload);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    // Kept for callers using the previous API; webhook code must use the byte overload.
    public static bool Validate(string payload, string signature, string appSecret)
        => Validate(Encoding.UTF8.GetBytes(payload), signature, appSecret);
}
