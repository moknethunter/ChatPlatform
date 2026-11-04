using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public enum WhatsAppSendState
{
    Accepted,
    Retryable,
    PermanentFailure,
    Unknown
}

public sealed record WhatsAppSendResult(
    WhatsAppSendState State,
    string? MessageId = null,
    int? HttpStatus = null,
    int? MetaErrorCode = null,
    string? Error = null,
    TimeSpan? RetryAfter = null
);

public interface IWhatsAppCloudClient
{
    Task<WhatsAppSendResult> SendTextAsync(
        string phoneNumberId,
        string recipient,
        string text,
        CancellationToken cancellationToken);
}

public sealed class WhatsAppCloudClient : IWhatsAppCloudClient
{
    private readonly IHttpClientFactory _factory;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppCloudClient> _logger;

    public WhatsAppCloudClient(
        IHttpClientFactory factory,
        IOptions<WhatsAppOptions> options,
        ILogger<WhatsAppCloudClient> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WhatsAppSendResult> SendTextAsync(
        string phoneNumberId,
        string recipient,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            return new(WhatsAppSendState.PermanentFailure,
                Error: "Missing access token");

        if (string.IsNullOrWhiteSpace(phoneNumberId) ||
            string.IsNullOrWhiteSpace(recipient) ||
            string.IsNullOrWhiteSpace(text))
            return new(WhatsAppSendState.PermanentFailure,
                Error: "Invalid message parameters");

        var version = _options.GraphApiVersion.Trim('/');
        var client = _factory.CreateClient("whatsapp");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{version}/{Uri.EscapeDataString(phoneNumberId)}/messages");

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer", _options.AccessToken);

        request.Content = JsonContent.Create(new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = recipient,
            type = "text",
            text = new
            {
                preview_url = false,
                body = text
            }
        });

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var json = JsonDocument.Parse(body);

                    var id = json.RootElement
                        .GetProperty("messages")[0]
                        .GetProperty("id")
                        .GetString();

                    if (string.IsNullOrWhiteSpace(id))
                        return new(WhatsAppSendState.Unknown,
                            HttpStatus: (int)response.StatusCode,
                            Error: "Meta accepted response without message ID");

                    return new(WhatsAppSendState.Accepted,
                        MessageId: id,
                        HttpStatus: (int)response.StatusCode);
                }
                catch (JsonException)
                {
                    return new(WhatsAppSendState.Unknown,
                        HttpStatus: (int)response.StatusCode,
                        Error: "Invalid Meta response");
                }
                catch (KeyNotFoundException)
                {
                    return new(WhatsAppSendState.Unknown,
                        HttpStatus: (int)response.StatusCode,
                        Error: "Missing Meta message ID");
                }
            }

            int? errorCode = null;

            try
            {
                using var json = JsonDocument.Parse(body);

                if (json.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("code", out var code) &&
                    code.TryGetInt32(out var parsed))
                {
                    errorCode = parsed;
                }
            }
            catch (JsonException)
            {
                // لا نسجل محتوى الاستجابة الخام.
            }

            var retryAfter = response.Headers.RetryAfter?.Delta;

            var retryable =
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500;

            _logger.LogWarning(
                "Meta API returned HTTP {Status}, error code {Code}",
                (int)response.StatusCode,
                errorCode);

            return new(
                retryable
                    ? WhatsAppSendState.Retryable
                    : WhatsAppSendState.PermanentFailure,
                HttpStatus: (int)response.StatusCode,
                MetaErrorCode: errorCode,
                Error: "Meta API rejected the request",
                RetryAfter: retryAfter);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException ||
            ex is OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "WhatsApp request outcome is unknown");

            // لا نعيد الإرسال تلقائياً؛ ربما وصلت الرسالة إلى Meta
            // ولكن انقطع الاتصال قبل وصول الاستجابة.
            return new(WhatsAppSendState.Unknown,
                Error: "Network outcome unknown");
        }
    }
}
