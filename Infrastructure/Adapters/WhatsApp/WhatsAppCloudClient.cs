using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed record WhatsAppSendResult(
    bool IsSuccess,
    bool IsRetryable,
    bool IsAmbiguous,
    string? MetaMessageId,
    string? ErrorCode,
    int? HttpStatusCode);

public interface IWhatsAppCloudClient
{
    Task<WhatsAppSendResult> SendAsync(
        string graphApiVersion,
        string phoneNumberId,
        string accessToken,
        string payloadJson,
        CancellationToken cancellationToken);
}

/// <summary>Thin HTTP client for WhatsApp Cloud API. Never logs tokens or response bodies.</summary>
public sealed class WhatsAppCloudClient : IWhatsAppCloudClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public WhatsAppCloudClient(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory;

    public async Task<WhatsAppSendResult> SendAsync(
        string graphApiVersion,
        string phoneNumberId,
        string accessToken,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(graphApiVersion) ||
            string.IsNullOrWhiteSpace(phoneNumberId) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            return new(false, false, false, null, "missing_credentials", null);
        }

        try
        {
            using var payloadDocument = JsonDocument.Parse(payloadJson);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{graphApiVersion.TrimEnd('/')}/{Uri.EscapeDataString(phoneNumberId)}/messages")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var client = _httpClientFactory.CreateClient("whatsapp");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (status == 429)
                    return new(false, true, false, null, "meta_rate_limited", status);

                // A server-side error can occur after Meta accepted the message; do not retry blindly.
                if (status >= 500)
                    return new(false, false, true, null, "meta_server_error_unknown_result", status);

                return new(false, false, false, null, $"meta_http_{status}", status);
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var result = JsonDocument.Parse(responseBody);
                if (!result.RootElement.TryGetProperty("messages", out var messages) ||
                    messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0 ||
                    !messages[0].TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    return new(false, false, true, null, "meta_success_without_message_id", (int)response.StatusCode);
                }

                return new(true, false, false, idElement.GetString(), null, (int)response.StatusCode);
            }
            catch (JsonException)
            {
                return new(false, false, true, null, "meta_invalid_success_response", (int)response.StatusCode);
            }
        }
        catch (JsonException)
        {
            return new(false, false, false, null, "invalid_outbox_payload", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The remote outcome is unknown. The outbox worker must not resend automatically.
            return new(false, false, true, null, "send_timeout_unknown_result", null);
        }
        catch (HttpRequestException)
        {
            return new(false, false, true, null, "network_error_unknown_result", null);
        }
    }
}
