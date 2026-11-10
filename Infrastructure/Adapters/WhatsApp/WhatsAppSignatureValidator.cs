using System.Security.Cryptography;
using System.Text;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppSignatureValidator
{
    public static bool Validate(byte[] payload, string signature, string appSecret)
    {
        if (payload == null || payload.Length == 0)
            return false;

        if (string.IsNullOrWhiteSpace(appSecret) || string.IsNullOrWhiteSpace(signature))
            return false;

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
            var hash = hmac.ComputeHash(payload);
            var expected = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
            var actual = signature.Trim();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(actual));
        }
        catch
        {
            return false;
        }
    }
}
