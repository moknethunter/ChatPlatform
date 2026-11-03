using System.Security.Cryptography;
using System.Text;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

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

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), payload);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    public static bool Validate(string payload, string signature, string appSecret)
        => Validate(Encoding.UTF8.GetBytes(payload), signature, appSecret);
}
