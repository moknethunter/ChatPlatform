using System.Security.Cryptography;
using System.Text;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppSignatureValidator
{
    public static bool Validate(string payload, string signature, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(appSecret) || string.IsNullOrWhiteSpace(signature))
            return false;

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var expected = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(signature));
        }
        catch
        {
            return false;
        }
    }
}
