using System.Security.Cryptography;
using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Core.Models;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Messaging;
using ChatPlatform.Infrastructure.Webhooks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PlatformDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default")
                ?? "Data Source=chatplatform.db"));
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
builder.Services.AddHostedService<RealtimeBroadcaster>();
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreated();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

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

var g = app.MapGroup("/api/subscriptions");

g.MapGet("/", async (PlatformDbContext db, CancellationToken ct) =>
    Results.Ok(await db.WebhookSubscriptions.AsNoTracking().ToListAsync(ct)));

g.MapPost("/", async (CreateSubRequest req, PlatformDbContext db, CancellationToken ct) =>
{
    if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var u)
        || (u.Scheme != "http" && u.Scheme != "https"))
        return Results.BadRequest();
    var s = new WebhookSubscription
    {
        Url = req.Url,
        Secret = string.IsNullOrWhiteSpace(req.Secret)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower()
            : req.Secret!,
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

// ---------- Records ----------
public record PublishEventRequest(string Type, string? Source, JsonElement? Data);
public record CreateSubRequest(string Url, string? Secret, string? EventTypes, string? Description);

// ---------- SignalR Hub ----------
public sealed class ChatHub : Hub
{
    private readonly IEventBus _bus;
    public ChatHub(IEventBus b) => _bus = b;

    public Task Join(string channel) => Groups.AddToGroupAsync(Context.ConnectionId, channel);
    public Task Leave(string channel) => Groups.RemoveFromGroupAsync(Context.ConnectionId, channel);

    public async Task Send(string channel, string text, string? senderId = null)
    {
        var evt = new PlatformEvent
        {
            Type = EventTypes.MessageCreated,
            Source = "chat",
            Payload = JsonSerializer.SerializeToElement(new
            {
                ChannelId = channel,
                Text = text,
                SenderId = senderId ?? Context.ConnectionId
            })
        };
        await _bus.PublishAsync(evt);
    }
}

// ---------- Realtime broadcaster ----------
public sealed class RealtimeBroadcaster : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IHubContext<ChatHub> _hub;
    public RealtimeBroadcaster(IEventBus b, IHubContext<ChatHub> h) => (_bus, _hub) = (b, h);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var e in _bus.SubscribeAsync(ct))
        {
            if (e.Type == EventTypes.MessageCreated
                && e.Payload.ValueKind == JsonValueKind.Object
                && e.Payload.TryGetProperty("ChannelId", out var ch)
                && ch.GetString() is string channel)
                await _hub.Clients.Group(channel).SendAsync("message.created", e, ct);
            else
                await _hub.Clients.All.SendAsync("platform.event", e, ct);
        }
    }
}
