using System.Security.Cryptography;
using System.Text;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppSignatureValidator
{
    private const string SignaturePrefix = "sha256=";
    private const int Sha256HashLength = 32;
    private const int SignatureLength = SignaturePrefix.Length + (Sha256HashLength * 2);

    public static bool Validate(
        ReadOnlySpan<byte> payload,
        string? signature,
        string? appSecret)
    {
        if (string.IsNullOrWhiteSpace(appSecret) ||
            string.IsNullOrWhiteSpace(signature) ||
            signature.Length != SignatureLength ||
            !signature.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] suppliedHash;
        try
        {
            suppliedHash = Convert.FromHexString(signature[SignaturePrefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (suppliedHash.Length != Sha256HashLength)
            return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var expectedHash = hmac.ComputeHash(payload.ToArray());

        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    // Compatibility overload for existing callers that still provide a string.
    public static bool Validate(
        string? payload,
        string? signature,
        string? appSecret)
    {
        if (payload is null)
            return false;

        return Validate(Encoding.UTF8.GetBytes(payload), signature, appSecret);
    }
}
