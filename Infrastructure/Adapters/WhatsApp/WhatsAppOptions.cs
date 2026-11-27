using Microsoft.Extensions.Configuration; // تمت الإضافة لاستخدام ConfigurationKeyName

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    // هذا المتغير سيُقرأ من appsettings.json أو من متغير البيئة WHATSAPP_GRAPH_API_VERSION
    public string GraphApiVersion { get; set; } = "v26.0";

    // ✅ تمت الإضافة: اسم بديل لنفس الإصدار - عشان Program.cs كان ينادي ApiVersion
    // وهو نفسه GraphApiVersion، أي تغيير في واحد يتغير في الثاني تلقائياً
    public string ApiVersion
    {
        get => GraphApiVersion;
        set => GraphApiVersion = string.IsNullOrWhiteSpace(value) ? "v26.0" : value;
    }

    // Program.cs normalizes the legacy environment aliases to canonical configuration keys.
    public string PhoneNumberId { get; set; } = "";

    // يربط الخاصية بالمتغير: WHATSAPP_BUSINESS_ACCOUNT_ID
    [ConfigurationKeyName("WHATSAPP_BUSINESS_ACCOUNT_ID")]
    public string BusinessAccountId { get; set; } = "";

    // ✅ تمت الإضافة: اسم بديل لـ BusinessAccountId - عشان بعض الكود ينادي WabaId
    [ConfigurationKeyName("WHATSAPP_WABA_ID")]
    public string WabaId
    {
        get => BusinessAccountId;
        set => BusinessAccountId = value ?? "";
    }

    // يربط الخاصية بالمتغير: Test_number
    [ConfigurationKeyName("Test_number")]
    public string TestNumber { get; set; } = "";

    // Canonical key: WhatsApp:AccessToken.
    public string AccessToken { get; set; } = "";

    // Canonical key: WhatsApp:VerifyToken.
    public string VerifyToken { get; set; } = "";

    // Canonical key: WhatsApp:AppSecret.
    public string AppSecret { get; set; } = "";

    // هذا المتغير سيُقرأ من appsettings.json أو من متغير البيئة WHATSAPP_AUTOREPLY
    public bool AutoReply { get; set; } = false;

    // ✅ تمت الإضافة: فحص سريع هل الإعدادات كاملة أم لا - بدون ما يوقف البرنامج
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessToken) &&
        !string.IsNullOrWhiteSpace(PhoneNumberId) &&
        !string.IsNullOrWhiteSpace(VerifyToken);
}
