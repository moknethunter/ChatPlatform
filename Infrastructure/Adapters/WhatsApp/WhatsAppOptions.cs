namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    public string GraphApiVersion { get; set; } = "v21.0";
    public string PhoneNumberId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string VerifyToken { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public bool AutoReply { get; set; } = false;
}
