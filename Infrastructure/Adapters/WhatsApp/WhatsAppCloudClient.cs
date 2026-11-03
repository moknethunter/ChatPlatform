using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    string? MetaMessageId = null,
    string? ErrorCode = null,
    int? HttpStatusCode = null,
    int? RetryAfterSeconds = null);

public interface IWhatsAppCloudClient
{
    Task<WhatsAppSendResult> SendAsync(
        string phoneNumberId,
        string accessToken,
        string payloadJson,
        CancellationToken cancellationToken);
}

public sealed class WhatsAppCloudClient : IWhatsAppCloudClient
{
    private static readonly Regex GraphVersionPattern = new(@"^v\d+\.\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WhatsAppOptions _options;

    public WhatsAppCloudClient(IHttpClientFactory httpClientFactory, IOptions<WhatsAppOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<WhatsAppSendResult> SendAsync(
        string phoneNumberId,
        string accessToken,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId) ||
            string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(payloadJson))
        {
            return new(WhatsAppSendState.PermanentFailure, ErrorCode: "invalid_request");
        }

        var version = _options.GraphApiVersion.Trim().Trim('/');
        if (!GraphVersionPattern.IsMatch(version))
            return new(WhatsAppSendState.PermanentFailure, ErrorCode: "invalid_graph_version");

        var client = _httpClientFactory.CreateClient("whatsapp");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{version}/{Uri.EscapeDataString(phoneNumberId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var messageId = ExtractMessageId(responseBody);
                return messageId is not null
                    ? new(WhatsAppSendState.Accepted, messageId, HttpStatusCode: (int)response.StatusCode)
                    : new(WhatsAppSendState.Unknown, ErrorCode: "success_without_wamid", HttpStatusCode: (int)response.StatusCode);
            }

            var retryAfter = GetRetryAfterSeconds(response);
            var errorCode = ExtractMetaErrorCode(responseBody) ?? $"http_{(int)response.StatusCode}";
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                response.StatusCode == HttpStatusCode.RequestTimeout ||
                (int)response.StatusCode >= 500)
            {
                return new(WhatsAppSendState.Retryable, ErrorCode: errorCode,
                    HttpStatusCode: (int)response.StatusCode, RetryAfterSeconds: retryAfter);
            }

            return new(WhatsAppSendState.PermanentFailure, ErrorCode: errorCode,
                HttpStatusCode: (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(WhatsAppSendState.Unknown, ErrorCode: "send_timeout");
        }
        catch (HttpRequestException)
        {
            // Meta may have accepted the request before the connection failed; do not retry automatically.
            return new(WhatsAppSendState.Unknown, ErrorCode: "network_result_unknown");
        }
    }

    private static string? ExtractMessageId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0 &&
                messages[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException) { }

        return null;
    }

    private static string? ExtractMetaErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code))
            {
                return code.ToString();
            }
        }
        catch (JsonException) { }

        return null;
    }

    private static int? GetRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
        if (retryAfter?.Date is { } date)
            return Math.Max(1, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        return null;
    }
}
