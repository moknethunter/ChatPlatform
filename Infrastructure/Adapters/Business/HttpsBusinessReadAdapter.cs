using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;

namespace ChatPlatform.Infrastructure.Adapters.Business;

// Operator-owned gateway protocol. Vendor-specific adapters can implement the same contract.
// بروتوكول وسيط موثوق؛ لا يدّعي التوافق المباشر مع كل مزود دفع أو محاسبة.
public sealed class HttpsBusinessReadAdapter(IHttpClientFactory clients) : IBusinessReadAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Provider => "https-gateway";

    public static bool IsAllowedEndpoint(string? raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
        uri.HostNameType == UriHostNameType.Dns && !uri.IsLoopback &&
        !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
        !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    public async Task<BusinessReadResult?> ReadAsync(BusinessReadSource source, BusinessReadKind kind,
        string reference, CancellationToken ct)
    {
        // Only a fixed operator-configured HTTPS endpoint is allowed, never a caller URL.
        if (!IsAllowedEndpoint(source.Endpoint)) throw new InvalidOperationException("Invalid gateway endpoint.");
        using var client = clients.CreateClient("business-read");
        using var request = new HttpRequestMessage(HttpMethod.Post, source.Endpoint);
        // Do not include secrets in URLs or logs / الأسرار في الترويسة فقط.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.Token);
        request.Content = JsonContent.Create(new { protocol = "chatplatform.business.read.v1",
            operation = source.Operations[kind], reference });
        // No automatic retries or redirects for third-party requests.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Gateway unavailable.");
        // Bound streamed responses even if Content-Length is absent / حد حجم الرد.
        const int max = 16384;
        if (response.Content.Headers.ContentLength > max) throw new InvalidOperationException("Response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (output.Length + read > max) throw new InvalidOperationException("Response too large.");
            output.Write(buffer,0,read);
        }
        return JsonSerializer.Deserialize<BusinessReadResult>(output.ToArray(), Json);
    }
}
