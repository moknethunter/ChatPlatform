using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

public sealed class WhatsAppMediaService(IHttpClientFactory factory)
{
    // Conservative project-wide limit to protect memory on small Fly.io instances.
    public const int MaxBytes = 5 * 1024 * 1024;
    public sealed record MediaKind(string Type, string Mime, string Filename);
    public sealed record Downloaded(byte[] Bytes, MediaKind Kind);

    public static bool ValidId(string? id) => id is { Length: >= 1 and <= 32 } && id.All(c => c is >= '0' and <= '9');
    public static bool TrustedDownload(Uri? uri) => uri != null && uri.IsAbsoluteUri &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.Equals("lookaside.fbsbx.com", StringComparison.OrdinalIgnoreCase);

    public static MediaKind? Detect(byte[] bytes)
    {
        ReadOnlySpan<byte> b = bytes;
        if (b.Length < 8 || b.Length > MaxBytes) return null;
        if (b.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return new("image", "image/jpeg", "image.jpg");
        if (b.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return new("image", "image/png", "image.png");
        if (b.StartsWith("%PDF-"u8)) return new("document", "application/pdf", "document.pdf");
        if (b.StartsWith("OggS"u8) && b[..Math.Min(b.Length, 256)].IndexOf("OpusHead"u8) >= 0)
            return new("audio", "audio/ogg", "audio.ogg");
        if (b.StartsWith("ID3"u8) || (b[0] == 0xff && (b[1] & 0xe0) == 0xe0))
            return new("audio", "audio/mpeg", "audio.mp3");
        // MP4 container only; Meta still validates the actual codec.
        if (b.Length >= 12 && b.Slice(4, 4).SequenceEqual("ftyp"u8)) return new("video", "video/mp4", "video.mp4");
        return null;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<byte[]?> ReadBounded(HttpContent content, int max, CancellationToken ct)
    {
        if (content.Headers.ContentLength > max) return null;
        await using var source = await content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (destination.Length + count > max) return null;
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return destination.ToArray();
    }

    public async Task<string?> UploadAsync(string phoneId, string token, byte[] bytes, MediaKind kind, CancellationToken ct)
    {
        if (!ValidId(phoneId) || Detect(bytes) != kind) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        ct = deadline.Token;
        using var client = factory.CreateClient("whatsapp-media");
        using var request = Authorized(HttpMethod.Post, phoneId + "/media", token);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("whatsapp"), "messaging_product");
        form.Add(new StringContent(kind.Mime), "type");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(kind.Mime);
        form.Add(file, "file", kind.Filename);
        request.Content = form;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        var data = await ReadBounded(response.Content, 65536, ct);
        if (data == null) return null;
        using var json = JsonDocument.Parse(data);
        return json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && ValidId(id.GetString())
            ? id.GetString() : null;
    }

    public async Task<Downloaded?> DownloadAsync(string phoneId, string mediaId, string token, string expectedType, CancellationToken ct)
    {
        if (!ValidId(phoneId) || !ValidId(mediaId)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        ct = deadline.Token;
        using var client = factory.CreateClient("whatsapp-media");
        using var metadata = Authorized(HttpMethod.Get, mediaId + "?phone_number_id=" + phoneId, token);
        using var response = await client.SendAsync(metadata, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        var data = await ReadBounded(response.Content, 65536, ct);
        if (data == null) return null;
        using var json = JsonDocument.Parse(data);
        if (!json.RootElement.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || !TrustedDownload(uri)) return null;
        using var download = Authorized(HttpMethod.Get, uri.AbsoluteUri, token);
        using var media = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!media.IsSuccessStatusCode) return null;
        var bytes = await ReadBounded(media.Content, MaxBytes, ct);
        if (bytes == null) return null;
        var kind = Detect(bytes);
        return kind == null || kind.Type != expectedType ? null : new(bytes, kind);
    }
}
