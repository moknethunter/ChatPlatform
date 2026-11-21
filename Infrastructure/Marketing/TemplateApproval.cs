using System.Net.Http.Headers;
using System.Text.Json;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Data.Entities;
namespace ChatPlatform.Infrastructure.Marketing;

public sealed class TemplateApproval(IHttpClientFactory factory)
{
    public static bool Eligible(WhatsAppTemplateEntity t)=>t.IsActive && t.Category=="MARKETING" &&
        t.Name is {Length:>=1 and <=100} && t.Name.All(c=>c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') &&
        t.Language is {Length:>=2 and <=20} && t.Language.All(c=>char.IsAsciiLetter(c)||c=='_') &&
        !string.IsNullOrWhiteSpace(t.Body) && t.Body.Length<=1024 && !t.Body.Contains("{{");
    public static bool Matches(JsonElement root,WhatsAppTemplateEntity t)
    {
        if(!root.TryGetProperty("data",out var data)||data.ValueKind!=JsonValueKind.Array)return false;
        foreach(var item in data.EnumerateArray())
        {
            if(item.GetString("name")!=t.Name||item.GetString("language")!=t.Language||item.GetString("status")!="APPROVED"||item.GetString("category")!="MARKETING")continue;
            if(!item.TryGetProperty("components",out var c)||c.ValueKind!=JsonValueKind.Array||c.GetArrayLength()!=1)continue;
            var body=c[0];if(body.GetString("type")=="BODY"&&body.GetString("text")==t.Body&&!t.Body.Contains("{{"))return true;
        }
        return false;
    }
    public async Task<bool> VerifyAsync(WhatsAppTenantEntity account,WhatsAppTemplateEntity template,string token,CancellationToken ct)
    {
        if(!Eligible(template)||!WhatsAppMediaService.ValidId(account.WabaId)||template.WhatsAppTenantId!=account.Id||template.TenantId!=account.TenantId)return false;
        try{
            using var client=factory.CreateClient("whatsapp-media");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var request=new HttpRequestMessage(HttpMethod.Get,account.WabaId+"/message_templates?name="+Uri.EscapeDataString(template.Name)+"&fields=name,language,status,category,components&limit=100");
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if(!response.IsSuccessStatusCode||response.Content.Headers.ContentLength>262144)return false;
            using var output=new MemoryStream();await using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] buffer=new byte[8192];int n;while((n=await stream.ReadAsync(buffer.AsMemory(),deadline.Token))>0){if(output.Length+n>262144)return false;await output.WriteAsync(buffer.AsMemory(0,n),deadline.Token);}
            using var json=JsonDocument.Parse(output.ToArray());return Matches(json.RootElement,template);
        }catch(Exception ex) when(ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or FormatException){return false;}
    }
}
