using System.Net;
using System.Security.Claims;
using ChatPlatform.Controllers;
using ChatPlatform.Infrastructure.Bots;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Collections.Concurrent;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Infrastructure.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

const string tenant = "smoke-tenant";
const string phoneId = "999999999999";
const string recipient = "967700000000"; // Synthetic only: all HTTP calls use the fake handler.
var directory = Directory.CreateTempSubdirectory("chatplatform-messaging-");
var connection = "Data Source=" + Path.Combine(directory.FullName, "smoke.db");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var ct = timeout.Token;
try
{
    if (!WhatsAppMediaService.TrustedDownload(new Uri("https://lookaside.fbsbx.com/whatsapp_business/attachments/")) ||
        WhatsAppMediaService.TrustedDownload(new Uri("https://lookaside.fbsbx.com.evil.invalid/")) ||
        WhatsAppMediaService.TrustedDownload(new Uri("http://lookaside.fbsbx.com/")) ||
        WhatsAppMediaService.TrustedDownload(new Uri("https://lookaside.fbsbx.com:8443/")) ||
        WhatsAppMediaService.Detect(System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>")) != null ||
        WhatsAppMediaService.Detect(new byte[] {137,80,78,71,13,10,26,10})?.Type != "image")
        throw new Exception("Media type or download host security regression.");
    Console.WriteLine("PASS: media signature checks and strict CDN URL allowlist.");
    // Verify approval matches the exact stored template; local status alone cannot authorize campaigns.
    var marketingTemplate=new WhatsAppTemplateEntity{Name="offer",Language="en",Category="MARKETING",Body="Offer",IsActive=true};
    using var approved=JsonDocument.Parse("""{"data":[{"name":"offer","language":"en","category":"MARKETING","status":"APPROVED","components":[{"type":"BODY","text":"Offer"}]}]}""");
    using var rejected=JsonDocument.Parse("""{"data":[{"name":"offer","language":"en","category":"MARKETING","status":"REJECTED","components":[{"type":"BODY","text":"Offer"}]}]}""");
    if(!ChatPlatform.Infrastructure.Marketing.TemplateApproval.Eligible(marketingTemplate) ||
        !ChatPlatform.Infrastructure.Marketing.TemplateApproval.Matches(approved.RootElement,marketingTemplate) ||
        ChatPlatform.Infrastructure.Marketing.TemplateApproval.Matches(rejected.RootElement,marketingTemplate))
        throw new Exception("Marketing template approval guard regression.");
    marketingTemplate.Body="Different";
    if(ChatPlatform.Infrastructure.Marketing.TemplateApproval.Matches(approved.RootElement,marketingTemplate))
        throw new Exception("Marketing template body mismatch accepted.");
    Console.WriteLine("PASS: exact approved marketing template and rejected/mismatched guards.");
    // Exercise the same schema creation/compatibility logic as production.
    DbInitializer.EnsureCreated(connection);
    var requests = new ConcurrentQueue<string>();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddOptions<WhatsAppOptions>();
    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
    services.AddDbContext<PlatformDbContext>(o => o.UseSqlite(connection));
    services.AddSingleton<IEventBus, InMemoryEventBus>();
    services.AddHttpClient("whatsapp", c => c.BaseAddress = new Uri("https://graph.invalid/"))
        .ConfigurePrimaryHttpMessageHandler(() => new FakeGraphHandler(requests));
    services.AddScoped<IWhatsAppCloudClient, WhatsAppCloudClient>();
    services.AddSingleton<WhatsAppAdapter>();
    services.AddSingleton<WhatsAppAutoReplyAdapter>();
    services.AddSingleton<WhatsAppOutboxWorker>();
    await using var provider = services.BuildServiceProvider();
    Guid conversationId;
    using (var scope = provider.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var dataProtection = provider.GetRequiredService<IDataProtectionProvider>();
        var protector = dataProtection.CreateProtector("WhatsApp.AccessTokens");
        var compatible = new WhatsAppTokenProtection(dataProtection);
        if (compatible.Unprotect(compatible.Protect("current-token")) != "current-token" ||
            compatible.Unprotect(protector.Protect("legacy-token")) != "legacy-token")
            throw new Exception("Token protection compatibility failed.");
        Console.WriteLine("PASS: current and legacy encrypted token formats are readable.");
        db.Tenants.Add(new TenantEntity { Id = tenant, Name = "Smoke test" });
        db.WhatsAppTenants.Add(new WhatsAppTenantEntity { TenantId = tenant, PhoneNumberId = phoneId,
            BusinessTokenEncrypted = protector.Protect("fake-token") });
        var conversation = new WhatsAppConversationEntity { TenantId = tenant, PhoneNumberId = phoneId,
            ContactPhone = recipient, LastInboundAtUtc = DateTime.UtcNow, WindowExpiresAtUtc = DateTime.UtcNow.AddHours(24) };
        conversationId = conversation.Id;
        db.WhatsAppConversations.Add(conversation);
        db.AutoReplyRules.Add(new AutoReplyRuleEntity { TenantId = tenant, Name = "Smoke rule",
            MatchType = "exact", KeywordPattern = "ping", ReplyText = "pong {message}", CooldownSeconds = 0 });
        await db.SaveChangesAsync(ct);
    }

    var adapter = provider.GetRequiredService<WhatsAppAdapter>();
    await adapter.HandleEventAsync(new PlatformEvent { Type = "whatsapp.send", Source = "smoke",
        Payload = JsonSerializer.SerializeToElement(new { phoneNumberId = phoneId, to = recipient, text = "manual smoke" }) }, ct);
    var bus = provider.GetRequiredService<IEventBus>();
    await using (var subscription = bus.SubscribeAsync(ct).GetAsyncEnumerator(ct))
    {
        // Begin listening before publishing, because the bus has no replay.
        var pending = subscription.MoveNextAsync().AsTask();
        await provider.GetRequiredService<WhatsAppAutoReplyAdapter>().HandleEventAsync(new PlatformEvent
        {
            Type = "whatsapp.inbound", Source = "smoke",
            Payload = JsonSerializer.SerializeToElement(new { conversationId, from = recipient, text = "ping", wamid = "wamid.smoke.inbound" })
        }, ct);
        if (!await pending || subscription.Current.Type != "whatsapp.send")
            throw new Exception("Auto-reply did not publish a send event.");
        await adapter.HandleEventAsync(subscription.Current, ct);
    }

    using (var scope = provider.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var ctrl = new MessagesController(db);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("tenant_id", tenant) }, "smoke")) };
        ctrl.ControllerContext = new ControllerContext { HttpContext = context };
        context.Request.Headers["Idempotency-Key"] = "ci-text";
        var text = new MessagesController.SendTextRequest(recipient, JsonSerializer.SerializeToElement("api smoke"), phoneId, tenant, null, "text");
        if (await ctrl.Send(text, ct) is not AcceptedResult || await ctrl.Send(text, ct) is not AcceptedResult)
            throw new Exception("Authenticated text queue or idempotency failed");
        context.Request.Headers["Idempotency-Key"] = "ci-template";
        var template = new MessagesController.SendTemplateRequest(recipient,
            JsonSerializer.SerializeToElement(new { name = "ci_example", language = new { code = "ar" } }), phoneId, tenant, null);
        if (await ctrl.SendTemplate(template, ct) is not AcceptedResult || await ctrl.SendTemplate(template, ct) is not AcceptedResult)
            throw new Exception("Authenticated template queue or idempotency failed");
        var mediaBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX9sAAAAASUVORK5CYII=");
        var mediaService = new WhatsAppMediaService(new FakeMediaFactory(mediaBytes));
        var protection = provider.GetRequiredService<IDataProtectionProvider>();
        var mediaController = new MediaController(db, mediaService, protection) { ControllerContext = new ControllerContext { HttpContext = context } };
        using var stream = new MemoryStream(mediaBytes);
        var upload = await mediaController.Upload(conversationId, new FormFile(stream, 0, mediaBytes.Length, "file", "pixel.png"), ct);
        if (upload is not OkObjectResult uploaded) throw new Exception("Mocked media upload failed");
        var ticket = JsonSerializer.SerializeToElement(uploaded.Value).GetProperty("ticket").GetString();
        context.Request.Headers["Idempotency-Key"] = "ci-image";
        var sendMedia = new MessagesController.SendMediaRequest(conversationId, ticket, "ci image");
        var acceptedMedia = await ctrl.SendMedia(sendMedia, protection, ct);
        if (acceptedMedia is not AcceptedResult || await ctrl.SendMedia(sendMedia, protection, ct) is not AcceptedResult)
            throw new Exception("Media send or idempotency failed");
        var image = await db.WhatsAppMessages.AsNoTracking().SingleAsync(m => m.ConversationId == conversationId && m.MessageType == "image", ct);
        var downloaded = await mediaController.Download(image.Id, false, ct);
        if (downloaded is not FileContentResult file || !file.FileContents.SequenceEqual(mediaBytes))
            throw new Exception("Mocked media download failed");
        // Verify the signed upload ticket is not transferable between tenants.
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", "foreign") }, "smoke"));
        if (await ctrl.SendMedia(sendMedia, protection, ct) is not ForbidResult ||
            await mediaController.Download(image.Id, false, ct) is not NotFoundResult)
            throw new Exception("Media tenant isolation failed");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", tenant) }, "smoke"));
        Console.WriteLine("PASS: mocked media upload, download, tenant-bound ticket and idempotent queue.");
        var conv = await db.WhatsAppConversations.SingleAsync(c => c.Id == conversationId, ct);
        var definition = new BotFlow.Definition("s", new BotFlow.Node[] {
            new("s","start",null,"r",null,0,0), new("r","reply","bot smoke","e",null,180,0), new("e","end",null,null,null,360,0) });
        db.BotReleases.Add(new BotReleaseEntity { TenantId = tenant, DraftId = Guid.NewGuid(), ActivationId = Guid.NewGuid(),
            Version = 1, Name = "Smoke bot", DefinitionJson = JsonSerializer.Serialize(definition,new JsonSerializerOptions(JsonSerializerDefaults.Web)), PublishedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        var inbound = new WhatsAppMessageEntity { TenantId = tenant, ConversationId = conversationId,
            Wamid = "wamid.runtime.smoke", Direction = "Inbound", MessageType = "text", TextPreview = "ping", ContentJson = "{}",
            Status = "Received", TimestampUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow };
        db.WhatsAppMessages.Add(inbound);
        await BotRuntime.StageAsync(db,conv,inbound,"ping",DateTime.UtcNow,ct);
        await BotRuntime.StageAsync(db,conv,inbound,"ping",DateTime.UtcNow,ct);
        await db.SaveChangesAsync(ct);
        if (await db.AutomationRuns.CountAsync(r => r.InboundMessageId == inbound.Id && r.Outcome == "queued",ct) != 1)
            throw new Exception("Durable automation receipt missing or duplicated");
        if (await db.WhatsAppOutbox.CountAsync(x => x.TenantId == tenant, ct) != 6)
            throw new Exception("Duplicate queue rows detected");
        Console.WriteLine("PASS: tenant-bound controller text/template queue; repeat keys reuse original message.");
        // Add two automated sends with ambiguous outcomes, one already claimed by a dead worker.
        foreach (var (label, expired) in new[] { ("ambiguous smoke", false), ("expired lease smoke", true) })
        {
            var payload = JsonSerializer.Serialize(new { messaging_product = "whatsapp", recipient_type = "individual",
                to = recipient, type = "text", text = new { body = label } });
            var outgoing = new WhatsAppMessageEntity { TenantId = tenant, ConversationId = conversationId,
                Direction = "Outbound", MessageType = "text", TextPreview = label, ContentJson = payload, Status = "Queued",
                TimestampUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow };
            db.WhatsAppMessages.Add(outgoing);
            db.WhatsAppOutbox.Add(new WhatsAppOutboxEntity { TenantId = tenant, PhoneNumberId = phoneId, RecipientPhone = recipient,
                IdempotencyKey = "automation:test:"+label, MessageId = outgoing.Id, PayloadJson = payload,
                Status = expired ? "Processing" : "Pending", LeaseOwner = expired ? "dead-worker" : null,
                LeaseUntilUtc = expired ? DateTime.UtcNow.AddSeconds(-5) : null,
                Attempts = expired ? 10 : 0, MaxAttempts = 10, NextAttemptAtUtc = DateTime.UtcNow });
        }
        await db.SaveChangesAsync(ct);
    }

    var worker = provider.GetRequiredService<WhatsAppOutboxWorker>();
    await worker.StartAsync(ct);
    try
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.WhatsAppOutbox.AsNoTracking().Where(x => x.TenantId == tenant).ToListAsync(ct);
            var messages = await db.WhatsAppMessages.AsNoTracking().Where(x => x.ConversationId == conversationId).ToListAsync(ct);
            if (rows.Count == 8 && rows.Count(x => x.Status == "Sent") == 6 &&
                rows.Count(x => x.Status == "DeliveryUnknown") == 2 &&
                messages.Count(m => m.Direction == "Outbound" && m.Status == "Sent" && m.Wamid != null) == 6 &&
                messages.Count(m => m.Direction == "Outbound" && m.Status == "DeliveryUnknown") == 2)
            {
                if (!requests.Contains("manual smoke") || !requests.Contains("pong ping") || !requests.Contains("api smoke") || !requests.Contains("ci_example") || !requests.Contains("ci image") || !requests.Contains("bot smoke") || requests.Count(x => x == "ambiguous smoke") != 1 || requests.Contains("expired lease smoke") || requests.Count != 7)
                    throw new Exception("Unexpected simulated Graph requests.");
                Console.WriteLine("PASS: six successful sends; ambiguous automated send attempted once and expired lease never resent.");
                break;
            }
            await Task.Delay(250, ct);
        }
    }
    finally { await worker.StopAsync(CancellationToken.None); }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    directory.Delete(true);
}

sealed class FakeGraphHandler(ConcurrentQueue<string> requests) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.Host != "graph.invalid" ||
            request.RequestUri.AbsolutePath != "/999999999999/messages" ||
            request.Headers.Authorization?.Parameter != "fake-token")
            throw new InvalidOperationException("Unexpected outgoing request.");
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var root = payload.RootElement;
        var label = root.GetProperty("type").GetString() switch {
            "template" => root.GetProperty("template").GetProperty("name").GetString()!,
            "image" => root.GetProperty("image").GetProperty("caption").GetString()!,
            _ => root.GetProperty("text").GetProperty("body").GetString()!
        };
        requests.Enqueue(label);
        if (label == "ambiguous smoke")
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("simulated ambiguous outcome") };
        // No network is used. Return a different synthetic ID for each accepted message.
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { messages = new[] { new { id = "wamid.smoke." + Guid.NewGuid().ToString("N") } } }))
        };
    }
}

sealed class FakeMediaFactory(byte[] bytes) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        if (name != "whatsapp-media") throw new InvalidOperationException("Unexpected media client");
        return new HttpClient(new FakeMediaHandler(bytes)) { BaseAddress = new Uri("https://graph.facebook.com/v26.0/") };
    }
}
sealed class FakeMediaHandler(byte[] bytes) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization?.Parameter != "fake-token") throw new Exception("Unexpected media credentials");
        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Post && uri.Host == "graph.facebook.com" && uri.AbsolutePath == "/v26.0/999999999999/media")
        {
            if (request.Content is not MultipartFormDataContent form || form.Count() != 3)
                throw new Exception("Invalid upload form");
            var file = form.Single(c => c.Headers.ContentDisposition?.Name?.Trim('"') == "file");
            if (!(await file.ReadAsByteArrayAsync(ct)).SequenceEqual(bytes)) throw new Exception("Upload bytes mismatch");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"123456789\"}") };
        }
        if (request.Method == HttpMethod.Get && uri.Host == "graph.facebook.com" && uri.AbsolutePath == "/v26.0/123456789" && uri.Query == "?phone_number_id=999999999999")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"url\":\"https://lookaside.fbsbx.com/whatsapp_business/attachments/123456789\"}") };
        if (request.Method == HttpMethod.Get && uri.Host == "lookaside.fbsbx.com")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        throw new Exception("Unexpected media HTTP request");
    }
}
