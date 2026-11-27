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
    var externalOne = "Data Source=" + Path.Combine(directory.FullName,"external-one.db");
    var externalTwo = "Data Source=" + Path.Combine(directory.FullName,"external-two.db");
    foreach (var pair in new[] { (Connection:externalOne, Value:"first"),(Connection:externalTwo, Value:"second") })
    {
        await using var external = new Microsoft.Data.Sqlite.SqliteConnection(pair.Connection);
        await external.OpenAsync(ct);
        await using var seed = external.CreateCommand();
        seed.CommandText="CREATE TABLE Items (Id TEXT PRIMARY KEY, Value TEXT); INSERT INTO Items VALUES ('test', @value);";
        seed.Parameters.AddWithValue("@value",pair.Value);
        await seed.ExecuteNonQueryAsync(ct);
    }
    var externalConfig = new Microsoft.Extensions.Configuration.ConfigurationManager();
    externalConfig["ConnectionStrings:one"]=externalOne;
    externalConfig["ConnectionStrings:two"]=externalTwo;
    foreach (var alias in new[] {"one","two"})
    {
        var prefix="ExternalDatabases:Tenants:smoke-tenant:"+alias+":";
        externalConfig[prefix+"Provider"]="sqlite";
        externalConfig[prefix+"ConnectionName"]=alias;
        externalConfig[prefix+"Reads:lookup"]="SELECT Value FROM Items WHERE Id=@input";
        externalConfig[prefix+"Reads:badwrite"]="DELETE FROM Items WHERE Id=@input RETURNING Value";
    }
    var registry = new ChatPlatform.Infrastructure.Data.Connections.ExternalDatabaseRegistry(externalConfig, new IDatabaseAdapter[] { new ChatPlatform.Infrastructure.Adapters.Databases.SqliteDatabaseAdapter(), new ChatPlatform.Infrastructure.Adapters.Databases.PostgreSqlDatabaseAdapter() });
    var results=await Task.WhenAll(
        Task.Run(()=>registry.ReadScalarAsync(tenant,"one","lookup","test",ct),ct),
        Task.Run(()=>registry.ReadScalarAsync(tenant,"two","lookup","test",ct),ct));
    if (results[0]!="first" || results[1]!="second" || registry.List(tenant).Count!=2 ||
        registry.List("other-tenant").Count!=0 || registry.Contains("other-tenant","one") ||
        await registry.ReadScalarAsync(tenant,"one","lookup","test' OR 1=1 --",ct)!=null)
        throw new Exception("External database routing or parameter isolation regression.");
    try { await registry.ProbeAsync("other-tenant","one",ct); throw new Exception("Cross-tenant connection allowed."); }
    catch (KeyNotFoundException) { }
    try { await registry.ReadScalarAsync(tenant,"one","badwrite","test",ct); throw new Exception("External database write allowed."); }
    catch (Microsoft.Data.Sqlite.SqliteException) { }
    if (await registry.ReadScalarAsync(tenant,"one","lookup","test",ct)!="first")
        throw new Exception("Read-only connection changed data.");
    externalConfig["ExternalDatabases:Tenants:smoke-tenant:two:Provider"]="test-adapter";
    var extensionRegistry = new ChatPlatform.Infrastructure.Data.Connections.ExternalDatabaseRegistry(externalConfig,
        new IDatabaseAdapter[] { new ChatPlatform.Infrastructure.Adapters.Databases.SqliteDatabaseAdapter(), new TestDatabaseAdapter() });
    if (await extensionRegistry.ReadScalarAsync(tenant,"two","lookup","test",ct)!="second" ||
        extensionRegistry.List(tenant).Single(x=>x.Id=="two").Provider!="test-adapter")
        throw new Exception("Independent database adapter registration failed.");
    Console.WriteLine("PASS: independent external databases, tenant routing, parameterized reads and write rejection.");

    // Both business adapters share the exact same tenant-scoped typed read contract.
    externalConfig["ExternalDatabases:Tenants:smoke-tenant:one:Reads:balance"] =
        "SELECT json_object('reference',Id,'amountMinor',12345,'currency','YER','status','available') FROM Items WHERE Id=@input";
    externalConfig["ExternalDatabases:Tenants:smoke-tenant:one:Reads:invoice"] =
        "SELECT json_object('reference',Id,'amountMinor',100,'currency','YER','status','open') FROM Items WHERE Id=@input";
    var businessDb = new ChatPlatform.Infrastructure.Data.Connections.ExternalDatabaseRegistry(externalConfig,
        new IDatabaseAdapter[] { new ChatPlatform.Infrastructure.Adapters.Databases.SqliteDatabaseAdapter(), new TestDatabaseAdapter() });
    var businessConfig = new Microsoft.Extensions.Configuration.ConfigurationManager();
    const string accountPrefix="BusinessIntegrations:Tenants:smoke-tenant:accounting:";
    const string paymentPrefix="BusinessIntegrations:Tenants:smoke-tenant:payments:";
    businessConfig[accountPrefix+"Provider"]="database";
    businessConfig[accountPrefix+"DatabaseAlias"]="one";
    businessConfig[accountPrefix+"Operations:customer-balance"]="balance";
    businessConfig[accountPrefix+"Operations:invoice"]="invoice";
    businessConfig[paymentPrefix+"Provider"]="https-gateway";
    businessConfig[paymentPrefix+"Endpoint"]="https://payments.example.invalid/read";
    businessConfig[paymentPrefix+"Token"]="ci-business-token";
    businessConfig[paymentPrefix+"Operations:payment-status"]="status";
    var fakeBusiness=new FakeBusinessFactory();
    var business=new ChatPlatform.Infrastructure.Adapters.Business.BusinessReadRegistry(businessConfig,
        new IBusinessReadAdapter[] {
            new ChatPlatform.Infrastructure.Adapters.Business.DatabaseBusinessReadAdapter(businessDb),
            new ChatPlatform.Infrastructure.Adapters.Business.HttpsBusinessReadAdapter(fakeBusiness)
        },businessDb);
    var balance=await business.ReadAsync(tenant,"accounting",BusinessReadKind.CustomerBalance,"test",ct);
    var payment=await business.ReadAsync(tenant,"payments",BusinessReadKind.PaymentStatus,"pay-123",ct);
    if(balance?.AmountMinor!=12345 || balance.Currency!="YER" || payment?.Status!="succeeded" ||
        business.List(tenant).Count!=2 || business.List("foreign").Count!=0 || fakeBusiness.Calls!=1)
        throw new Exception("Business adapter contract regression.");
    try { await business.ReadAsync("foreign","payments",BusinessReadKind.PaymentStatus,"pay-123",ct); throw new Exception("Cross-tenant read accepted."); }
    catch(KeyNotFoundException) { }
    try { await business.ReadAsync(tenant,"payments",BusinessReadKind.Invoice,"pay-123",ct); throw new Exception("Unapproved operation accepted."); }
    catch(KeyNotFoundException) { }
    try { await business.ReadAsync(tenant,"payments",BusinessReadKind.PaymentStatus,"' OR 1=1",ct); throw new Exception("Invalid reference accepted."); }
    catch(ArgumentException) { }
    if(fakeBusiness.Calls!=1)throw new Exception("Unauthorized request reached gateway.");
    fakeBusiness.Body="""{"reference":"wrong","amountMinor":100,"currency":"YER","status":"succeeded"}""";
    try { await business.ReadAsync(tenant,"payments",BusinessReadKind.PaymentStatus,"pay-123",ct); throw new Exception("Wrong reference accepted."); }
    catch(InvalidOperationException) { }
    fakeBusiness.Body=new string('x',16385);
    try { await business.ReadAsync(tenant,"payments",BusinessReadKind.PaymentStatus,"pay-123",ct); throw new Exception("Oversized result accepted."); }
    catch(InvalidOperationException) { }
    if(ChatPlatform.Infrastructure.Adapters.Business.HttpsBusinessReadAdapter.IsAllowedEndpoint("http://example.invalid/read") ||
        ChatPlatform.Infrastructure.Adapters.Business.HttpsBusinessReadAdapter.IsAllowedEndpoint("https://127.0.0.1/read") ||
        ChatPlatform.Infrastructure.Adapters.Business.HttpsBusinessReadAdapter.IsAllowedEndpoint("https://user@example.invalid/read"))
        throw new Exception("Unsafe gateway address accepted.");
    Console.WriteLine("PASS: business database and mocked payment lookup, tenant isolation, operation scope, response bounds and reference verification.");

    // A trusted invoice link and exact money are mandatory even for manual review.
    var inv=new BusinessReadResult("inv-1",100,"YER","open");
    var pay=new BusinessReadResult("pay-1",100,"YER","succeeded","inv-1");
    Func<BusinessReadResult?,BusinessReadResult?,ChatPlatform.Infrastructure.Adapters.Business.ReconciliationResult> compare=ChatPlatform.Infrastructure.Adapters.Business.PaymentReconciliation.Compare;
    if(compare(inv,pay).Outcome!="matched_for_review" ||
       compare(inv,pay with {InvoiceReference=null}).Outcome!="invoice_link_unverified" ||
       compare(inv,pay with {InvoiceReference="inv-2"}).Outcome!="invoice_link_unverified" ||
       compare(inv,pay with {Currency="USD"}).Outcome!="currency_mismatch" ||
       compare(inv,pay with {Status="refunded"}).Outcome!="payment_not_succeeded" ||
       compare(inv with {Status="void"},pay).Outcome!="invoice_requires_review" ||
       compare(inv,pay with {AmountMinor=99}).Outcome!="amount_mismatch" ||
       compare(null,pay).Outcome!="record_unavailable")
        throw new Exception("Payment reconciliation safety regression.");
    businessConfig["PaymentReconciliation:Tenants:smoke-tenant:pair:InvoiceSource"]="accounting";
    businessConfig["PaymentReconciliation:Tenants:smoke-tenant:pair:PaymentSource"]="payments";
    var reconciler=new ChatPlatform.Infrastructure.Adapters.Business.PaymentReconciliation(businessConfig,business);
    fakeBusiness.Body="""{"reference":"pay-123","amountMinor":100,"currency":"YER","status":"succeeded","invoiceReference":"test"}""";
    var matched=await reconciler.CompareAsync(tenant,"pair","test","pay-123",ct);
    if(matched.Outcome!="matched_for_review"||reconciler.List(tenant).Count!=1||reconciler.List("foreign").Count!=0)
        throw new Exception("Configured reconciliation failed.");
    var callsBefore=fakeBusiness.Calls;
    try{await reconciler.CompareAsync("foreign","pair","test","pay-123",ct);throw new Exception("Foreign reconciliation accepted.");}
    catch(KeyNotFoundException){}
    if(fakeBusiness.Calls!=callsBefore)throw new Exception("Foreign comparison contacted gateway.");
    Console.WriteLine("PASS: payment comparison requires exact invoice linkage, status, currency and amount; no writes.");
    var attempts = new EmbeddedSignupAttemptStore();
    var instant = DateTimeOffset.UtcNow;
    var first = attempts.Start("tenant-a", "session-a", "123", "456", instant)!;
    if (attempts.Consume(first, "tenant-b", "session-a", "123", "456", instant) ||
        attempts.Consume(first, "tenant-a", "session-b", "123", "456", instant) ||
        attempts.Consume(first, "tenant-a", "session-a", "999", "456", instant) ||
        attempts.Consume(first, "tenant-a", "session-a", "123", "999", instant) ||
        !attempts.Consume(first, "tenant-a", "session-a", "123", "456", instant) ||
        attempts.Consume(first, "tenant-a", "session-a", "123", "456", instant))
        throw new Exception("Signup attempt isolation or replay regression.");
    var expiredAttempt = attempts.Start("tenant-a", "session-a", "123", "456", instant)!;
    if (attempts.Consume(expiredAttempt, "tenant-a", "session-a", "123", "456", instant.AddMinutes(5)))
        throw new Exception("Expired signup accepted.");
    var superseded = attempts.Start("tenant-a", "session-a", "123", "456", instant)!;
    var current = attempts.Start("tenant-a", "session-a", "123", "456", instant)!;
    if (attempts.Consume(superseded, "tenant-a", "session-a", "123", "456", instant) ||
        !attempts.Consume(current, "tenant-a", "session-a", "123", "456", instant))
        throw new Exception("Superseded signup accepted.");
    Console.WriteLine("PASS: signup attempts bound to tenant/session/config, expire and cannot replay.");

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
    // Synthetic older EF schema: no CreatedAtUtc column, existing legacy token,
    // inactive disconnected account, and an original account timestamp.
    var legacyConnection = "Data Source=" + Path.Combine(directory.FullName, "legacy.db");
    using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection(legacyConnection))
    {
        legacy.Open();
        using var cmd = legacy.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE WhatsAppTenants (
                Id TEXT PRIMARY KEY, TenantId TEXT NOT NULL, PhoneNumberId TEXT NOT NULL,
                IsActive INTEGER NOT NULL, CreatedAt TEXT, WabaId TEXT,
                AccessTokenEncrypted TEXT, BusinessTokenEncrypted TEXT, AppId TEXT
            );
            INSERT INTO WhatsAppTenants VALUES
                ('legacy-active','tenant-alhawbani-001','123450',1,'2024-02-03 04:05:06',NULL,'legacy-token',NULL,NULL),
                ('legacy-inactive','tenant-alhawbani-001','123451',0,'2024-02-04 04:05:06',NULL,'revoked-token','',NULL),
                ('legacy-current','tenant-alhawbani-001','123452',1,'2024-02-05 04:05:06','123','old-token','new-token','456');
            """;
        cmd.ExecuteNonQuery();
    }
    string? baseline = null;
    for (var pass = 0; pass < 2; pass++)
    {
        DbInitializer.EnsureCreated(legacyConnection);
        await using var legacyDb = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(legacyConnection).Options);
        var rows = await legacyDb.WhatsAppTenants.AsNoTracking().Where(x => x.Id.StartsWith("legacy-")).OrderBy(x => x.Id).ToListAsync(ct);
        if (rows.Count != 3 || rows[0].CreatedAt.Year != 2024 || rows[0].WabaId != "" || rows[0].AppId != "" ||
            rows[0].BusinessTokenEncrypted != "legacy-token" || rows[1].Id != "legacy-current" ||
            rows[1].BusinessTokenEncrypted != "new-token" || rows[2].BusinessTokenEncrypted != "" || rows[2].IsActive ||
            rows.Any(x => x.TenantId != "tenant-alhawbani-001"))
            throw new Exception("Legacy migration lost data or restored an inactive token.");
        var snapshot = JsonSerializer.Serialize(rows);
        if (baseline != null && baseline != snapshot) throw new Exception("Migration was not idempotent.");
        baseline = snapshot;
    }
    Console.WriteLine("PASS: migration twice on legacy EF schema preserves dates, ownership and current credentials.");
    // Exercise the same schema creation/compatibility logic as production.
    DbInitializer.EnsureCreated(connection);
    // Two independent contexts stand in for distinct application processes using the same DB.
    var sharedOptions = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
    await using (var firstDb = new PlatformDbContext(sharedOptions))
    await using (var secondDb = new PlatformDbContext(sharedOptions))
    {
        var writer = new DatabaseSignupAttemptStore(firstDb);
        var reader = new DatabaseSignupAttemptStore(secondDb);
        var moment = DateTimeOffset.UtcNow;
        var shared = await writer.StartAsync("tenant-a","session-a","123","456",moment,ct);
        if (shared == null ||
            await reader.ConsumeAsync(shared,"tenant-b","session-a","123","456",moment,ct) ||
            await reader.ConsumeAsync(shared,"tenant-a","session-b","123","456",moment,ct) ||
            await reader.ConsumeAsync(shared,"tenant-a","session-a","999","456",moment,ct) ||
            !await reader.ConsumeAsync(shared,"tenant-a","session-a","123","456",moment,ct) ||
            await writer.ConsumeAsync(shared,"tenant-a","session-a","123","456",moment,ct))
            throw new Exception("Shared signup isolation or single-use regression.");
        var prior = await writer.StartAsync("tenant-a","session-a","123","456",moment,ct);
        var latest = await reader.StartAsync("tenant-a","session-a","123","456",moment,ct);
        if (await writer.ConsumeAsync(prior,"tenant-a","session-a","123","456",moment,ct) ||
            !await writer.ConsumeAsync(latest,"tenant-a","session-a","123","456",moment,ct))
            throw new Exception("Shared signup supersession regression.");
        var expiring = await writer.StartAsync("tenant-a","session-a","123","456",moment,ct);
        if (await reader.ConsumeAsync(expiring,"tenant-a","session-a","123","456",moment.AddMinutes(5),ct))
            throw new Exception("Expired shared signup accepted.");
        var racing = await writer.StartAsync("tenant-a","session-a","123","456",moment,ct);
        var outcomes = await Task.WhenAll(
            Task.Run(() => writer.ConsumeAsync(racing,"tenant-a","session-a","123","456",moment,ct),ct),
            Task.Run(() => reader.ConsumeAsync(racing,"tenant-a","session-a","123","456",moment,ct),ct));
        if (outcomes.Count(x=>x)!=1) throw new Exception("Shared signup consumed more or less than once.");
        Console.WriteLine("PASS: durable signup state across independent contexts, atomic single-use, isolation and expiry.");
    }

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

    // Isolated account repair test: fake Graph confirms phone membership, not just WABA existence.
    using (var scope = provider.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var dp = provider.GetRequiredService<IDataProtectionProvider>();
        var account = new WhatsAppTenantEntity { Id = "repair-account", TenantId = tenant, PhoneNumberId = "123456",
            WabaId = "", BusinessTokenEncrypted = "", IsActive = true };
        db.WhatsAppTenants.Add(account);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var resolver = new ChatPlatform.Infrastructure.Adapters.WhatsApp.Binding.WhatsAppTenantResolver(db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChatPlatform.Infrastructure.Adapters.WhatsApp.Binding.WhatsAppTenantResolver>.Instance);
        var controller = new WhatsAppController(db, new FakeAccountFactory(), resolver, dp,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WhatsAppController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        void Identity(string tenantId) => controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim("tenant_id", tenantId), new Claim(ClaimTypes.Role, "Admin") }, "smoke"));
        Identity("foreign");
        if (await controller.UpdateToken(account.Id, new UpdateTokenRequest("verified-token", "789"), ct) is not NotFoundResult)
            throw new Exception("Cross-tenant account repair accepted");
        if (await controller.VerifySavedAccount(account.Id, ct) is not NotFoundResult)
            throw new Exception("Cross-tenant verification accepted");
        Identity(tenant);
        if (await controller.VerifySavedAccount(account.Id, ct) is not ConflictObjectResult)
            throw new Exception("Verification accepted incomplete account");
        if (await controller.UpdateToken(account.Id, new UpdateTokenRequest("verified-token"), ct) is not BadRequestObjectResult ||
            await controller.UpdateToken(account.Id, new UpdateTokenRequest("verified-token", "790"), ct) is not BadRequestObjectResult ||
            await controller.UpdateToken(account.Id, new UpdateTokenRequest("verified-token", "789"), ct) is not OkObjectResult)
            throw new Exception("Missing, mismatched or valid WABA repair incorrect");
        db.ChangeTracker.Clear();
        var repaired = await db.WhatsAppTenants.AsNoTracking().SingleAsync(x => x.Id == account.Id, ct);
        if (repaired.WabaId != "789" || new WhatsAppTokenProtection(dp).Unprotect(repaired.BusinessTokenEncrypted) != "verified-token")
            throw new Exception("Verified repair was not persisted");
        if (await controller.VerifySavedAccount(account.Id, ct) is not OkObjectResult verified ||
            !JsonSerializer.SerializeToElement(verified.Value).GetProperty("valid").GetBoolean() ||
            JsonSerializer.Serialize(verified.Value).Contains("verified-token", StringComparison.Ordinal))
            throw new Exception("Stored-token verification failed or disclosed the token");
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationManager();
        cfg["Meta:AppId"] = "123";
        if (await controller.StartEmbeddedSignup(cfg, attempts, ct) is not ConflictObjectResult ||
            await controller.CompleteEmbeddedSignup(new WhatsAppController.EmbeddedSignupRequest("fake","fake","789","123456"),cfg,attempts,ct) is not ConflictObjectResult)
            throw new Exception("Embedded signup not disabled by default.");

        var ready = await controller.AccountReadiness(account.Id, cfg, ct) as OkObjectResult;
        if (ready == null) throw new Exception("Readiness unavailable");
        var readyJson = JsonSerializer.SerializeToElement(ready.Value);
        if (!readyJson.GetProperty("membershipVerified").GetBoolean() ||
            !readyJson.GetProperty("appSubscribed").GetBoolean() ||
            readyJson.GetProperty("deliveryVerified").ValueKind != JsonValueKind.Null)
            throw new Exception("Incorrect subscription or delivery readiness");
        cfg["Meta:EmbeddedSignupEnabled"] = "true";
        cfg["Meta:EmbeddedSignupConfigId"] = "456";
        cfg["Meta:AppSecret"] = "ci-fake-secret";
        void SignupIdentity(string tenantId, string sessionId) => controller.ControllerContext.HttpContext.User =
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", tenantId), new Claim(ClaimTypes.Role, "Admin"),
                new Claim("platform_session", sessionId) }, "smoke"));
        SignupIdentity(tenant, "session-one");
        var started = await controller.StartEmbeddedSignup(cfg, attempts, ct) as OkObjectResult;
        if (started == null) throw new Exception("Unable to start test signup.");
        var state = JsonSerializer.SerializeToElement(started.Value).GetProperty("state").GetString()!;
        var completion = new WhatsAppController.EmbeddedSignupRequest(state, "ci-code", "789", "654321");
        SignupIdentity(tenant, "session-two");
        if (await controller.CompleteEmbeddedSignup(completion, cfg, attempts, ct) is not BadRequestObjectResult)
            throw new Exception("Signup accepted across sessions.");
        SignupIdentity(tenant, "session-one");
        var completed = await controller.CompleteEmbeddedSignup(completion, cfg, attempts, ct) as OkObjectResult;
        if (completed == null) throw new Exception("Mocked signup failed.");
        var createdId = JsonSerializer.SerializeToElement(completed.Value).GetProperty("id").GetString()!;
        var created = await db.WhatsAppTenants.AsNoTracking().SingleAsync(x => x.Id == createdId, ct);
        if (created.TenantId != tenant || created.PhoneNumberId != "654321" || created.WabaId != "789" ||
            new WhatsAppTokenProtection(dp).Unprotect(created.BusinessTokenEncrypted) != "verified-token" ||
            JsonSerializer.Serialize(completed.Value).Contains("verified-token", StringComparison.Ordinal) ||
            await controller.CompleteEmbeddedSignup(completion, cfg, attempts, ct) is not BadRequestObjectResult)
            throw new Exception("Signup persistence, secrecy or replay regression.");
        await controller.DeleteAccount(createdId, ct);
        db.ChangeTracker.Clear();
        Identity(tenant);
        Console.WriteLine("PASS: mocked code exchange, verified account creation, session isolation, encrypted token and replay rejection.");
        cfg["Meta:AppId"] = "456";
        var absent = (OkObjectResult)await controller.AccountReadiness(account.Id, cfg, ct);
        if (JsonSerializer.SerializeToElement(absent.Value).GetProperty("appSubscribed").GetBoolean())
            throw new Exception("Unknown app reported subscribed");
        Identity("foreign");
        if (await controller.AccountReadiness(account.Id, cfg, ct) is not NotFoundResult)
            throw new Exception("Cross-tenant readiness accepted");
        Identity(tenant);
        await db.Database.ExecuteSqlRawAsync("UPDATE WhatsAppTenants SET BusinessTokenEncrypted = 'invalid!' WHERE Id = 'repair-account'", ct);
        if (await controller.VerifySavedAccount(account.Id, ct) is not ConflictObjectResult)
            throw new Exception("Unreadable saved token was not handled");
        if (await controller.UpdateToken(account.Id, new UpdateTokenRequest("verified-token", "790"), ct) is not ConflictObjectResult)
            throw new Exception("Established WABA could be replaced");
        await db.Database.ExecuteSqlRawAsync("UPDATE WhatsAppTenants SET AccessTokenEncrypted = 'old-token' WHERE Id = 'repair-account'", ct);
        if (await controller.DeleteAccount(account.Id, ct) is not OkObjectResult)
            throw new Exception("Disconnect failed");
        db.ChangeTracker.Clear();
        DbInitializer.EnsureCreated(connection);
        repaired = await db.WhatsAppTenants.AsNoTracking().SingleAsync(x => x.Id == account.Id, ct);
        if (repaired.IsActive || repaired.BusinessTokenEncrypted != "" ||
            await controller.UpdateToken(account.Id, new UpdateTokenRequest("verified-token", "789"), ct) is not ConflictObjectResult ||
            await controller.UpdateToken(account.Id, new UpdateTokenRequest("verified-token", "789", true), ct) is not OkObjectResult)
            throw new Exception("Disconnect restarted or explicit reconnection failed");
        // Do not leave the extra account active for the unrelated messaging regression tests.
        db.ChangeTracker.Clear();
        await controller.DeleteAccount(account.Id, ct);
        Console.WriteLine("PASS: legacy WABA repair, saved-token verification, cross-tenant rejection, disconnect survives restart, explicit reconnect.");
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

sealed class FakeAccountFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        if (name is not ("whatsapp" or "whatsapp-oauth")) throw new InvalidOperationException("Unexpected client");
        return new HttpClient(new FakeAccountHandler()) { BaseAddress = new Uri("https://graph.invalid/") };
    }
}
sealed class FakeAccountHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "graph.invalid")
            throw new InvalidOperationException("Unexpected account verification request");
        var uri = request.RequestUri!;
        if (uri.AbsolutePath == "/oauth/access_token")
        {
            if (uri.Query != "?client_id=123&client_secret=ci-fake-secret&code=ci-code")
                throw new InvalidOperationException("Unexpected synthetic OAuth request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"access_token\":\"verified-token\"}") });
        }
        if (request.Headers.Authorization?.Parameter != "verified-token")
            throw new InvalidOperationException("Unexpected account credentials");
        string body;
        if (uri.AbsolutePath == "/789/subscribed_apps" && uri.Query == "")
            body = "{\"data\":[{\"whatsapp_business_api_data\":{\"id\":\"123\"}}]}";
        else if (uri.AbsolutePath == "/789/phone_numbers" && uri.Query == "?fields=id&limit=100")
            body = "{\"data\":[{\"id\":\"999999\"}],\"paging\":{\"next\":\"https://untrusted.invalid/never-follow\",\"cursors\":{\"after\":\"page+2\"}}}";
        else if (uri.AbsolutePath == "/789/phone_numbers" && uri.Query == "?fields=id&limit=100&after=page%2B2")
            body = "{\"data\":[{\"id\":\"123456\"},{\"id\":\"654321\"}]}";
        else if (uri.AbsolutePath == "/790/phone_numbers" &&
            (uri.Query == "?fields=id&limit=100" || uri.Query == "?fields=id&limit=100&after=repeat"))
            body = "{\"data\":[{\"id\":\"999999\"}],\"paging\":{\"next\":\"https://untrusted.invalid/never-follow\",\"cursors\":{\"after\":\"repeat\"}}}";
        else
            throw new InvalidOperationException("Unexpected account verification path or cursor");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

sealed class TestDatabaseAdapter : IDatabaseAdapter
{
    private readonly ChatPlatform.Infrastructure.Adapters.Databases.SqliteDatabaseAdapter _inner = new();
    public string Provider => "test-adapter";
    public System.Data.Common.DbConnection CreateReadOnlyConnection(string connectionString) => _inner.CreateReadOnlyConnection(connectionString);
    public ValueTask<System.Data.Common.DbTransaction> BeginReadOnlyTransactionAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
        => _inner.BeginReadOnlyTransactionAsync(connection,ct);
}

sealed class FakeBusinessFactory : IHttpClientFactory
{
    public int Calls;
    public string Body="""{"reference":"pay-123","amountMinor":100,"currency":"YER","status":"succeeded"}""";
    public HttpClient CreateClient(string name) => name=="business-read"
        ? new HttpClient(new Handler(this)) : throw new InvalidOperationException("Unexpected client");
    private sealed class Handler(FakeBusinessFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            owner.Calls++;
            if(request.Method!=HttpMethod.Post || request.RequestUri?.AbsoluteUri!="https://payments.example.invalid/read" ||
                request.Headers.Authorization?.Parameter!="ci-business-token")throw new Exception("Unexpected gateway request");
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if(json.RootElement.GetProperty("operation").GetString()!="status" ||
                json.RootElement.GetProperty("reference").GetString()!="pay-123")throw new Exception("Unexpected gateway payload");
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(owner.Body)};
        }
    }
}
