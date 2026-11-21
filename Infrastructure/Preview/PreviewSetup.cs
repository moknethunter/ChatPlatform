using System.Net;
using System.Text.Json;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using ChatPlatform.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http;

namespace ChatPlatform.Infrastructure.Preview;

public static class PreviewSetup
{
    public const string AppName="chatplatform-preview-moknethunter-20261121";
    public const string Tenant="preview-tenant";
    public const string PhoneId="999999999991";
    private const string TestToken="preview-only-no-real-meta-token";
    public static void Configure(WebApplicationBuilder b)
    {
        if(Environment.GetEnvironmentVariable("FLY_APP_NAME") is string name && name!=AppName)
            throw new InvalidOperationException("Preview may only run on its dedicated Fly app.");
        // Never mount or use the production DB or production provider credentials.
        const string dir="/tmp/chatplatform-preview";
        Environment.SetEnvironmentVariable("CHATPLATFORM_DATA_DIR",dir);
        b.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+dir+"/platform.db";
        foreach(var k in new[]{"WHATSAPP_TOKEN","WHATSAPP_PHONE_NUMBER_ID","WHATSAPP_VERIFY_TOKEN","WHATSAPP_APP_SECRET","WhatsApp__AccessToken","WhatsApp__PhoneNumberId","WhatsApp__VerifyToken","WhatsApp__AppSecret"})
            Environment.SetEnvironmentVariable(k,null);
        foreach(var k in new[]{"AccessToken","PhoneNumberId","VerifyToken","AppSecret"})b.Configuration["WhatsApp:"+k]="";
        b.Configuration["OwnModel:Enabled"]="false";b.Configuration["OwnModel:Endpoint"]="";b.Configuration["OwnModel:Token"]="";
        b.Configuration["Campaigns:Enabled"]="false";
        foreach(var a in b.Configuration.GetSection("Security:Accounts").GetChildren())b.Configuration[a.Path+":Role"]="";
        // These hashes represent independent, randomly generated preview-only passwords.
        SetAccount(0,"preview-admin",Tenant,"Admin","AQAAAAIAAYagAAAAEMOTz8frCMXUFZ7W+VSiTLttRjN+xz4do5xpQnKgTfEt2KCrvM/z5U1AR/IHutctzg==");
        SetAccount(1,"preview-owner",PlatformSecurity.PlatformScope,"SuperAdmin","AQAAAAIAAYagAAAAEJxAnLUn6TO03SrJRtc9PUaNiwezD+ZeRn5tborpqHUnPC5qz/WohVwXfXbTVYikxA==");
        // A local smoke run may supply a disposable test hash, never an override on Fly.
        if(Environment.GetEnvironmentVariable("FLY_APP_NAME")==null && b.Configuration["Preview:SmokePasswordHash"] is string testHash && testHash.Length>30)
            b.Configuration["Security:Accounts:0:PasswordHash"]=testHash;
        void SetAccount(int i,string user,string tenant,string role,string hash)
        {
            var prefix=$"Security:Accounts:{i}:";
            b.Configuration[prefix+"Username"]=user;b.Configuration[prefix+"TenantId"]=tenant;
            b.Configuration[prefix+"Role"]=role;b.Configuration[prefix+"PasswordHash"]=hash;
        }
    }
    public static void Register(IServiceCollection s)
    {
        // Block every factory-created network client after its configuration, including
        // Meta verification/media, arbitrary connectors, and the own-model endpoint.
        s.AddSingleton<IHttpMessageHandlerBuilderFilter,OfflineFilter>();
        s.AddScoped<IWhatsAppCloudClient,SimulatedCloud>();
    }
    public sealed class OfflineFilter:IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)=>builder=>{
            next(builder);builder.PrimaryHandler=new OfflineHandler();
            builder.AdditionalHandlers.Clear();
        };
    }
    public sealed class OfflineHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            =>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("Preview: external network disabled")});
    }
    public sealed class SimulatedCloud:IWhatsAppCloudClient
    {
        public Task<WhatsAppSendResult> SendRawAsync(string phoneNumberId,string accessToken,string payloadJson,CancellationToken cancellationToken=default)
            =>Task.FromResult(phoneNumberId==PhoneId&&accessToken==TestToken
             ?new WhatsAppSendResult(true,false,"preview."+Guid.NewGuid().ToString("N"),null)
             :new WhatsAppSendResult(false,false,null,"preview_only"));
    }
    public static async Task SeedAsync(PlatformDbContext db,IDataProtectionProvider dp)
    {
        if(await db.Tenants.AnyAsync(t=>t.Id==Tenant))return;
        var now=DateTime.UtcNow;
        // Data protection persists its first key using a separate scoped connection.
        var encrypted=new WhatsAppTokenProtection(dp).Protect(TestToken);
        await using var tx=await db.Database.BeginTransactionAsync();
        var tenant=new TenantEntity{Id=Tenant,Name="متجر العرض التجريبي"};
        var account=new WhatsAppTenantEntity{Id="preview-account",TenantId=Tenant,PhoneNumberId=PhoneId,WabaId="999999999992",BusinessTokenEncrypted=encrypted,IsActive=true};
        var plan=new ServicePlanEntity{Name="الباقة التجريبية",MonthlyMessages=1000,Agents=10,Accounts=2,Bots=10,Articles=100,Campaigns=false,OwnModel=false,MonthlyPriceMinor=0,Currency="USD"};
        db.Tenants.Add(tenant);db.WhatsAppTenants.Add(account);db.ServicePlans.Add(plan);
        db.TenantSubscriptions.Add(new TenantSubscriptionEntity{TenantId=Tenant,PlanId=plan.Id,EndsAtUtc=now.AddDays(30)});
        db.KnowledgeArticles.Add(new KnowledgeArticleEntity{TenantId=Tenant,Title="سياسة التوصيل التجريبية",Body="التوصيل خلال يومين. جميع هذه البيانات وهمية لاختبار المنصة.",IsPublished=true,UpdatedAtUtc=now});
        db.WhatsAppTemplates.Add(new WhatsAppTemplateEntity{TenantId=Tenant,WhatsAppTenantId=account.Id,Name="preview_offer",Language="ar",Category="MARKETING",Body="مرحبًا، هذا قالب تجريبي فقط."});
        var customers=new[]{
            ("15550101001","أحمد — تجريبي","مرحبًا، متى يصل طلبي؟","سيصل طلبك خلال يومين بإذن الله.","lead"),
            ("15550101002","سارة — تجريبي","هل توجد عروض على المنتجات؟","نعم، يسعدنا مساعدتك في اختيار المنتج.","customer"),
            ("15550101003","محمد — تجريبي","أحتاج مساعدة من الدعم الفني.","بالتأكيد، سنحوّلك إلى الفريق المختص.","new"),
            ("15550101004","ليان — تجريبي","شكرًا على سرعة الرد.","أهلًا وسهلًا بك دائمًا.","customer"),
            ("15550101005","خالد — تجريبي","أريد معرفة الأسعار.","يمكنك طلب قائمة الأسعار هنا.","lead")};
        foreach(var c in customers)db.CrmContacts.Add(new CrmContactEntity{TenantId=Tenant,Phone=c.Item1,DisplayName=c.Item2,Stage=c.Item5,Tags="تجريبي",Notes="عميل وهمي فقط",UpdatedAtUtc=now,CreatedAtUtc=now});
        await db.SaveChangesAsync();
        for(int i=0;i<customers.Length;i++){
            var c=customers[i];var at=now.AddMinutes(-5-i*12);
            var conv=new WhatsAppConversationEntity{TenantId=Tenant,PhoneNumberId=PhoneId,ContactPhone=c.Item1,ContactName=c.Item2,LastInboundAtUtc=at,LastOutboundAtUtc=at.AddMinutes(1),WindowExpiresAtUtc=at.AddHours(24),UpdatedAtUtc=at.AddMinutes(1),CreatedAtUtc=at.AddHours(-1),UnreadCount=i%2==0?1:0,Status=i==3?"closed":"open"};
            db.WhatsAppConversations.Add(conv);
            db.WhatsAppMessages.Add(new WhatsAppMessageEntity{TenantId=Tenant,ConversationId=conv.Id,Direction="Inbound",MessageType="text",TextPreview=c.Item3,ContentJson=JsonSerializer.Serialize(new{type="text",text=new{body=c.Item3}}),Status="Received",Wamid="preview-in-"+i,TimestampUtc=at,CreatedAtUtc=at});
            db.WhatsAppMessages.Add(new WhatsAppMessageEntity{TenantId=Tenant,ConversationId=conv.Id,Direction="Outbound",MessageType="text",TextPreview=c.Item4,ContentJson=JsonSerializer.Serialize(new{type="text",text=new{body=c.Item4}}),Status=i%2==0?"Read":"Delivered",Wamid="preview-out-"+i,TimestampUtc=at.AddMinutes(1),CreatedAtUtc=at.AddMinutes(1)});
        }
        var flow=new ChatPlatform.Infrastructure.Bots.BotFlow.Definition("start",new ChatPlatform.Infrastructure.Bots.BotFlow.Node[]{
            new("start","start",null,"wait",null,30,30),new("wait","wait","هل تريد الأسعار أم الدعم؟","condition",null,30,150),
            new("condition","contains","الدعم","human","reply",300,150),new("human","handoff",null,null,null,560,30),
            new("reply","reply","هذه إجابة تجريبية للأسعار.","end",null,560,220),new("end","end",null,null,null,800,220)});
        db.BotDrafts.Add(new BotDraftEntity{TenantId=Tenant,Name="بوت الترحيب التجريبي",DefinitionJson=JsonSerializer.Serialize(flow,new JsonSerializerOptions(JsonSerializerDefaults.Web)),UpdatedAtUtc=now});
        db.CustomerLists.Add(new CustomerListEntity{TenantId=Tenant,Name="قائمة العملاء التجريبية",PhonesJson=JsonSerializer.Serialize(customers.Select(c=>c.Item1).ToArray())});
        await db.SaveChangesAsync();await tx.CommitAsync();
    }
}
