using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. إعداد قاعدة البيانات والسيرفر
builder.Services.AddDbContext<PlatformDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=chatplatform.db"));

// 2. إعداد المنصة (Event Bus والـ Webhooks)
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
builder.Services.AddHostedService<RealtimeBroadcaster>();
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));

// 3. إعداد تطبيق الشات (SignalR) والملفات الثابتة
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreated();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseDefaultFiles(); // لتشغيل التطبيق
app.UseStaticFiles();  // لتشغيل التطبيق

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

// استقبال الأحداث من الخارج
app.MapPost("/api/events", async (PublishEventRequest req, IEventBus bus) =>
{
    if (string.IsNullOrWhiteSpace(req.Type)) return Results.BadRequest();
    var evt = new PlatformEvent
    {
        Type = req.Type,
        Source = req.Source ?? "external",
        Payload = req.Data ?? JsonSerializer.SerializeToElement(new { })
    };
    await bus.PublishAsync(evt);
    return Results.Accepted($"/api/events/{evt.Id}", evt);
});

// إدارة الـ Webhooks
var g = app.MapGroup("/api/subscriptions");
g.MapGet("/", async (PlatformDbContext db, CancellationToken ct) =>
    Results.Ok(await db.WebhookSubscriptions.AsNoTracking().ToListAsync(ct)));

g.MapPost("/", async (CreateSubRequest req, PlatformDbContext db, CancellationToken ct) =>
{
    if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
        return Results.BadRequest();
    var s = new WebhookSubscription
    {
        Url = req.Url,
        Secret = string.IsNullOrWhiteSpace(req.Secret) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower() : req.Secret!,
        EventTypes = req.EventTypes ?? "*",
        Description = req.Description
    };
    db.WebhookSubscriptions.Add(s);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/subscriptions/{s.Id}", s);
});

g.MapDelete("/{id:guid}", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
{
    var s = await db.WebhookSubscriptions.FindAsync(new object?[] { id }, ct);
    if (s is null) return Results.NotFound();
    db.WebhookSubscriptions.Remove(s);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapHub<ChatHub>("/hub/chat");
app.Run();

// =====================================================================
// الأكواد الداخلية للمنصة (Types, Infrastructure, Hub)
// =====================================================================

public record PublishEventRequest(string Type, string? Source, JsonElement? Data);
public record CreateSubRequest(string Url, string? Secret, string? EventTypes, string? Description);

public sealed record PlatformEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Type { get; init; }
    public required string Source { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public JsonElement Payload { get; init; }
}

public static class EventTypes
{
    public const string MessageCreated = "message.created";
    public const string Custom = "custom.event";
}

public interface IEventBus
{
    ValueTask PublishAsync(PlatformEvent evt, CancellationToken ct = default);
    IAsyncEnumerable<PlatformEvent> SubscribeAsync(CancellationToken ct);
}

public sealed class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = default!;
    public string Secret { get; set; } = default!;
    public string EventTypes { get; set; } = "*";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public string? LastDeliveryStatus { get; set; }
}

public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<PlatformEvent>> _subs = new();
    public async ValueTask PublishAsync(PlatformEvent evt, CancellationToken ct = default)
    {
        foreach (var ch in _subs.Values)
            try { await ch.Writer.WriteAsync(evt, ct); } catch (ChannelClosedException) { }
    }
    public async IAsyncEnumerable<PlatformEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<PlatformEvent>(new UnboundedChannelOptions { SingleReader = true });
        _subs[id] = ch;
        try { await foreach (var e in ch.Reader.ReadAllAsync(ct)) yield return e; }
        finally { _subs.TryRemove(id, out _); ch.Writer.TryComplete(); }
    }
}

public sealed class PlatformDbContext : DbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> o) : base(o) { }
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    protected override void OnModelCreating(ModelBuilder mb) =>
        mb.Entity<WebhookSubscription>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Url).IsRequired().HasMaxLength(2048);
            e.Property(x => x.Secret).IsRequired().HasMaxLength(256);
            e.Property(x => x.EventTypes).IsRequired().HasMaxLength(512);
            e.HasIndex(x => x.IsActive);
        });
}

public static class HmacSigner
{
    public static string Sign(string payload, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

public sealed class WebhookDispatcher : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IServiceScopeFactory _sf;
    private readonly IHttpClientFactory _hf;
    public WebhookDispatcher(IEventBus b, IServiceScopeFactory sf, IHttpClientFactory hf) => (_bus, _sf, _hf) = (b, sf, hf);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var e in _bus.SubscribeAsync(ct))
        {
            try
            {
                using var scope = _sf.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var subs = await db.WebhookSubscriptions.Where(s => s.IsActive).ToListAsync(ct);
                var match = subs.Where(s => s.EventTypes == "*" || s.EventTypes.Split(',', StringSplitOptions.TrimEntries).Contains(e.Type)).ToList();
                if (match.Count == 0) continue;

                var payload = JsonSerializer.Serialize(e);
                var client = _hf.CreateClient("webhooks");

                foreach (var s in match)
                {
                    var sig = HmacSigner.Sign(payload, s.Secret);
                    using var r = new HttpRequestMessage(HttpMethod.Post, s.Url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                    r.Headers.Add("X-Webhook-Signature", sig);
                    r.Headers.Add("X-Webhook-Event", e.Type);
                    using var resp = await client.SendAsync(r, ct);
                    s.LastDeliveryAt = DateTimeOffset.UtcNow;
                    s.LastDeliveryStatus = ((int)resp.StatusCode).ToString();
                }
                await db.SaveChangesAsync(ct);
            }
            catch { }
        }
    }
}

public sealed class ChatHub : Hub
{
    private readonly IEventBus _bus;
    public ChatHub(IEventBus b) => _bus = b;
    public Task Join(string c) => Groups.AddToGroupAsync(Context.ConnectionId, c);
    public async Task Send(string c, string t) =>
        await _bus.PublishAsync(new PlatformEvent
        {
            Type = EventTypes.MessageCreated,
            Source = "chat",
            Payload = JsonSerializer.SerializeToElement(new { ChannelId = c, Text = t, SenderId = Context.ConnectionId })
        });
}

public sealed class RealtimeBroadcaster : BackgroundService
{
    private readonly IEventBus _b;
    private readonly IHubContext<ChatHub> _h;
    public RealtimeBroadcaster(IEventBus b, IHubContext<ChatHub> h) => (_b, _h) = (b, h);
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var e in _b.SubscribeAsync(ct))
        {
            if (e.Type == EventTypes.MessageCreated && e.Payload.TryGetProperty("ChannelId", out var ch) && ch.GetString() is string c)
                await _h.Clients.Group(c).SendAsync("message.created", e, ct);
            else
                await _h.Clients.All.SendAsync("platform.event", e, ct);
        }
    }
}
