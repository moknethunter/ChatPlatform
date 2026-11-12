using System.Security.Cryptography;
using System.Text;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public static class WhatsAppSignatureValidator
{
    private const string SignaturePrefix = "sha256=";

    public static bool Validate(byte[] payload, string signature, string appSecret)
    {
        // فحص سريع للتأكد من وجود البيانات
        if (payload == null || payload.Length == 0 || 
            string.IsNullOrWhiteSpace(signature) || 
            string.IsNullOrWhiteSpace(appSecret))
            return false;

        signature = signature.Trim();

        // التأكد من أن التوقيع يبدأ بـ sha256= 
        if (!signature.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] suppliedHash;
        try
        {
            // تحويل النص السداسي عشر إلى مصفوفة بايتات
            suppliedHash = Convert.FromHexString(signature[SignaturePrefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        // التأكد من أن طول الـ Hash هو 32 بايت (خاص بـ SHA256)
        if (suppliedHash.Length != 32)
            return false;

        // 🚀 التحسين الذكي: استخدام الدالة الثابتة لتجنب إنشاء كائنات جديدة في الذاكرة
        var expectedHash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(appSecret), 
            payload);

        // مقارنة آمنة ضد هجمات التوقيت
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}
