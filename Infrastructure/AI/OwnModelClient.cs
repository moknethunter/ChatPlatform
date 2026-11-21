using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatPlatform.Infrastructure.AI;

// No provider SDK, commercial model or fallback. Only the operator's fixed endpoint.
public sealed class OwnModelClient(IConfiguration cfg, IHttpClientFactory clients)
{
    public record ModelRequest(string Protocol, string Question, string Locale, KnowledgeSearch.Match[] Knowledge);
    public record ModelResponse(string? Answer);
    private Uri? Endpoint()
    {
        if (!cfg.GetValue<bool>("OwnModel:Enabled")) return null;
        if (!Uri.TryCreate(cfg["OwnModel:Endpoint"], UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) return null;
        return uri;
    }
    public bool IsConfigured => Endpoint() != null && !string.IsNullOrWhiteSpace(cfg["OwnModel:Token"]);
    public async Task<string?> AskAsync(string question, KnowledgeSearch.Match[] knowledge, CancellationToken ct)
    {
        var endpoint = Endpoint();
        var token = cfg["OwnModel:Token"];
        if (endpoint == null || string.IsNullOrWhiteSpace(token)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = clients.CreateClient("own-model");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new ModelRequest("chatplatform.own-model.v1", question, "ar", knowledge));
        using var result = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!result.IsSuccessStatusCode || result.Content.Headers.ContentLength > 32768) return null;
        await using var input = await result.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int size;
        while ((size = await input.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0)
        {
            if (output.Length + size > 32768) return null;
            await output.WriteAsync(buffer.AsMemory(0, size), deadline.Token);
        }
        var answer = JsonSerializer.Deserialize<ModelResponse>(output.ToArray(),new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Answer;
        return answer is { Length: >= 1 and <= 4000 } && !string.IsNullOrWhiteSpace(answer) ? answer : null;
    }
}
