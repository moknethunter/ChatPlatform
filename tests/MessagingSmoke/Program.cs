using System.Net;
using System.Security.Claims;
using ChatPlatform.Controllers;
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
        if (await db.WhatsAppOutbox.CountAsync(x => x.TenantId == tenant, ct) != 4)
            throw new Exception("Duplicate queue rows detected");
        Console.WriteLine("PASS: tenant-bound controller text/template queue; repeat keys reuse original message.");
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
            if (rows.Count == 4 && rows.All(x => x.Status == "Sent") &&
                messages.Count == 4 && messages.All(x => x.Status == "Sent" && x.Wamid != null))
            {
                if (!requests.Contains("manual smoke") || !requests.Contains("pong ping") || !requests.Contains("api smoke") || !requests.Contains("ci_example") || requests.Count != 4)
                    throw new Exception("Unexpected simulated Graph requests.");
                Console.WriteLine("PASS: manual send, auto-reply, authenticated text and template reached mocked Graph API; all marked Sent.");
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
        requests.Enqueue(root.GetProperty("type").GetString() == "template" ? root.GetProperty("template").GetProperty("name").GetString()! : root.GetProperty("text").GetProperty("body").GetString()!);
        // No network is used. Return a different synthetic ID for each accepted message.
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { messages = new[] { new { id = "wamid.smoke." + Guid.NewGuid().ToString("N") } } }))
        };
    }
}
