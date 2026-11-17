using Microsoft.Extensions.Configuration; // تمت الإضافة لاستخدام ConfigurationKeyName

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    // هذا المتغير سيُقرأ من appsettings.json أو من متغير البيئة WHATSAPP_GRAPH_API_VERSION
    public string GraphApiVersion { get; set; } = "v21.0";

    // يربط الخاصية بالمتغير: WHATSAPP_PHONE_NUMBER_ID
    [ConfigurationKeyName("WHATSAPP_PHONE_NUMBER_ID")]
    public string PhoneNumberId { get; set; } = "";

    // يربط الخاصية بالمتغير: WHATSAPP_BUSINESS_ACCOUNT_ID
    [ConfigurationKeyName("WHATSAPP_BUSINESS_ACCOUNT_ID")]
    public string BusinessAccountId { get; set; } = "";

    // يربط الخاصية بالمتغير: Test_number
    [ConfigurationKeyName("Test_number")]
    public string TestNumber { get; set; } = "";

    // يربط الخاصية بالمتغير: WHATSAPP_TOKEN
    [ConfigurationKeyName("WHATSAPP_TOKEN")]
    public string AccessToken { get; set; } = "";

    // يربط الخاصية بالمتغير: WHATSAPP_VERIFY_TOKEN
    [ConfigurationKeyName("WHATSAPP_VERIFY_TOKEN")]
    public string VerifyToken { get; set; } = "";

    // يربط الخاصية بالمتغير: WHATSAPP_APP_SECRET
    [ConfigurationKeyName("WHATSAPP_APP_SECRET")]
    public string AppSecret { get; set; } = "";

    // هذا المتغير سيُقرأ من appsettings.json أو من متغير البيئة WHATSAPP_AUTOREPLY
    public bool AutoReply { get; set; } = false;
}
