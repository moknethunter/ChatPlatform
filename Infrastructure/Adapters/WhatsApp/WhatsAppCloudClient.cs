using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppCloudClient : IWhatsAppCloudClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WhatsAppCloudClient> _logger;

    public WhatsAppCloudClient(
        IHttpClientFactory httpFactory,
        ILogger<WhatsAppCloudClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<WhatsAppSendResult> SendRawAsync(
        string phoneNumberId,
        string accessToken,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        var client = _httpFactory.CreateClient("whatsapp");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Uri.EscapeDataString(phoneNumberId)}/messages")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new WhatsAppSendResult(
                    IsSuccess: true,
                    IsTransient: false,
                    MetaMessageId: TryExtractMetaId(body),
                    Raw: body);
            }

            var statusCode = (int)response.StatusCode;
            var isTransient = statusCode == 429 || statusCode >= 500;

            _logger.LogWarning(
                "WhatsApp API returned HTTP {StatusCode}",
                statusCode);

            return new WhatsAppSendResult(
                IsSuccess: false,
                IsTransient: isTransient,
                MetaMessageId: null,
                Raw: $"http_{statusCode}: {Truncate(body, 500)}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "WhatsApp request failed due to a network error");
            return new WhatsAppSendResult(
                IsSuccess: false,
                IsTransient: true,
                MetaMessageId: null,
                Raw: "network_error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("WhatsApp request timed out");
            return new WhatsAppSendResult(
                IsSuccess: false,
                IsTransient: true,
                MetaMessageId: null,
                Raw: "timeout");
        }
    }

    private static string? TryExtractMetaId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0
                && messages[0].TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }
        catch (JsonException)
        {
            // Successful HTTP responses can still include unexpected payloads.
        }

        return null;
    }

    /// <summary>التحقق من توقيع Webhook على البايتات الخام.</summary>
    public static bool VerifySignature(byte[] rawBody, string? signature, string? appSecret)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || string.IsNullOrWhiteSpace(appSecret)
            || !signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signature[7..]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var expected = hmac.ComputeHash(rawBody);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
