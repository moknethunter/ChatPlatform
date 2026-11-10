namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public interface IWhatsAppCloudClient
{
    Task<WhatsAppSendResult> SendRawAsync(
        string phoneNumberId,
        string accessToken,
        string payloadJson,
        CancellationToken cancellationToken = default);
}

public sealed record WhatsAppSendResult(
    bool IsSuccess,
    bool IsTransient,
    string? MetaMessageId,
    string? Raw);
