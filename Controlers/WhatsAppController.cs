using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/whatsapp")]
public class WhatsAppController : ControllerBase
{
// تخزين مؤقت - لين تربطه بقاعدة البيانات حقك
// tenantId -> بيانات الربط
private static readonly ConcurrentDictionary<string, WhatsAppConnection> _connections = new();
// tenantId -> قائمة الرسائل
private static readonly ConcurrentDictionary<string, List<WhatsAppMessage>> _messages = new();

private readonly IConfiguration _config;  
private readonly IHttpClientFactory _http;  
private readonly ILogger<WhatsAppController> _logger;  

public WhatsAppController(IConfiguration config, IHttpClientFactory http, ILogger<WhatsAppController> logger)  
{  
    _config = config;  
    _http = http;  
    _logger = logger;  
}  

// ============ 1. تحقق ميتا من الـ Webhook ============  
// هذا هو اللي ميتا تتصل عليه اول مرة  
// GET /api/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=XXX&hub.challenge=XXX  
[HttpGet("webhook")]  
public IActionResult VerifyWebhook(  
    [FromQuery(Name = "hub.mode")] string mode,  
    [FromQuery(Name = "hub.verify_token")] string verifyToken,  
    [FromQuery(Name = "hub.challenge")] string challenge)  
{  
    var expected = _config["WhatsApp:VerifyToken"] ?? _config["WHATSAPP_VERIFY_TOKEN"] ?? "mokco-verify-123";  

    if (mode == "subscribe" && verifyToken == expected)  
    {  
        _logger.LogInformation("Webhook verified");  
        return Ok(challenge);  
    }  
    return Forbid();  
}  

// ============ 2. استقبال رسائل الواتساب من ميتا ============  
// POST /api/whatsapp/webhook  
[HttpPost("webhook")]  
public async Task<IActionResult> ReceiveWebhook([FromBody] JsonElement body)  
{  
    try  
    {  
        _logger.LogInformation("Webhook received: {Body}", body.GetRawText());  

        // ميتا ترسل entry -> changes -> value -> messages  
        if (body.TryGetProperty("entry", out var entries))  
        {  
            foreach (var entry in entries.EnumerateArray())  
            {  
                if (!entry.TryGetProperty("changes", out var changes)) continue;  
                foreach (var change in changes.EnumerateArray())  
                {  
                    if (!change.TryGetProperty("value", out var value)) continue;  

                    // رقم الواتساب اللي وصلت عليه الرسالة - نستخدمه كـ tenant  
                    string phoneNumberId = value.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("phone_number_id", out var pnid)  
                        ? pnid.GetString() ?? "default"  
                        : "default";  

                    // نطلع الـ tenantId من الـ phone_number_id  
                    string tenantId = GetTenantByPhoneId(phoneNumberId);  

                    if (value.TryGetProperty("messages", out var msgs))  
                    {  
                        foreach (var m in msgs.EnumerateArray())  
                        {  
                            var msg = new WhatsAppMessage  
                            {  
                                Id = m.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString(),  
                                From = m.TryGetProperty("from", out var from) ? from.GetString() ?? "" : "",  
                                Text = m.TryGetProperty("text", out var text) && text.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",  
                                Type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text",  
                                Timestamp = DateTime.UtcNow,  
                                Direction = "in",  
                                TenantId = tenantId  
                            };  

                            // لو رسالة صورة / صوت / فيديو - خزن اسم النوع  
                            if (msg.Type != "text" && string.IsNullOrEmpty(msg.Text))  
                                msg.Text = $"[{msg.Type}]";  

                            var list = _messages.GetOrAdd(tenantId, _ => new List<WhatsAppMessage>());  
                            lock (list) { list.Add(msg); }  

                            _logger.LogInformation("Saved message from {From} to {Tenant}: {Text}", msg.From, tenantId, msg.Text);  
                        }  
                    }  

                    // حالات الرسائل - تم الارسال / تم الاستلام / تمت القراءة  
                    if (value.TryGetProperty("statuses", out var statuses))  
                    {  
                        foreach (var s in statuses.EnumerateArray())  
                        {  
                            _logger.LogInformation("Status: {Status} for {Id}",   
                                s.TryGetProperty("status", out var st) ? st.GetString() : "?",  
                                s.TryGetProperty("id", out var sid) ? sid.GetString() : "?");  
                        }  
                    }  
                }  
            }  
        }  
        return Ok();  
    }  
    catch (Exception ex)  
    {  
        _logger.LogError(ex, "Webhook error");  
        return Ok(); // لازم نرجع Ok دايما عشان ميتا ما تعيد الارسال  
    }  
}  

// ============ 3. حفظ الربط - هذا اللي يضغط عليه زر "حفظ الربط" ============  
// POST /api/whatsapp/save  
// Body: { tenantId, phoneNumber, phoneNumberId, accessToken, verifyToken }  
[HttpPost("save")]  
public IActionResult SaveConnection([FromBody] SaveConnectionDto dto)  
{  
    if (string.IsNullOrEmpty(dto.TenantId) || string.IsNullOrEmpty(dto.AccessToken))  
        return BadRequest(new { ok = false, message = "TenantId و AccessToken مطلوبة" });  

    _connections[dto.TenantId] = new WhatsAppConnection  
    {  
        TenantId = dto.TenantId,  
        PhoneNumber = dto.PhoneNumber ?? "",  
        PhoneNumberId = dto.PhoneNumberId ?? "",  
        AccessToken = dto.AccessToken,  
        VerifyToken = dto.VerifyToken ?? "",  
        IsConnected = true,  
        ConnectedAt = DateTime.UtcNow  
    };  

    _logger.LogInformation("WhatsApp connected for tenant {Tenant} number {Number}", dto.TenantId, dto.PhoneNumber);  
    return Ok(new { ok = true, message = "تم حفظ الربط بنجاح ✅", tenantId = dto.TenantId });  
}  

// نفس الحفظ بس باسم ثاني - لان بعض الواجهات تنادي connect  
[HttpPost("connect")]  
public IActionResult Connect([FromBody] SaveConnectionDto dto) => SaveConnection(dto);  

// ============ 4. حالة الربط - عشان الواجهة تعرف هل مربوط ولا لا ============  
// GET /api/whatsapp/status?tenantId=tenant-mokco-001  
[HttpGet("status")]  
public IActionResult GetStatus([FromQuery] string tenantId)  
{  
    if (string.IsNullOrEmpty(tenantId)) tenantId = "tenant-mokco-001";  
    if (_connections.TryGetValue(tenantId, out var c))  
        return Ok(new { ok = true, connected = true, phoneNumber = c.PhoneNumber, phoneNumberId = c.PhoneNumberId, connectedAt = c.ConnectedAt });  
    return Ok(new { ok = true, connected = false, phoneNumber = "", phoneNumberId = "" });  
}  

// ============ 5. جلب الرسائل للانبوكس ============  
// GET /api/whatsapp/messages?tenantId=xxx  
[HttpGet("messages")]  
public IActionResult GetMessages([FromQuery] string tenantId)  
{  
    if (string.IsNullOrEmpty(tenantId)) tenantId = "tenant-mokco-001";  
    if (_messages.TryGetValue(tenantId, out var list))  
    {  
        lock (list) { return Ok(new { ok = true, messages = list.OrderByDescending(x => x.Timestamp).Take(100).ToList() }); }  
    }  
    return Ok(new { ok = true, messages = new List<WhatsAppMessage>() });  
}  

// ============ 6. ارسال رسالة من الانبوكس ============  
// POST /api/whatsapp/send  
// Body: { tenantId, to, text }  
[HttpPost("send")]  
public async Task<IActionResult> SendMessage([FromBody] SendMessageDto dto)  
{  
    if (!_connections.TryGetValue(dto.TenantId, out var conn))  
        return BadRequest(new { ok = false, message = "الواتساب غير مربوط لهذا المتجر، اربطه اولاً من صفحة connectors" });  

    var client = _http.CreateClient();  
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", conn.AccessToken);  

    var payload = new  
    {  
        messaging_product = "whatsapp",  
        recipient_type = "individual",  
        to = dto.To,  
        type = "text",  
        text = new { preview_url = false, body = dto.Text }  
    };  

    var res = await client.PostAsJsonAsync($"https://graph.facebook.com/v21.0/{conn.PhoneNumberId}/messages", payload);  
    var resBody = await res.Content.ReadAsStringAsync();  

    if (!res.IsSuccessStatusCode)  
    {  
        _logger.LogError("Send failed: {Body}", resBody);  
        return BadRequest(new { ok = false, message = "فشل الارسال", detail = resBody });  
    }  

    // حفظ الرسالة المرسلة  
    var list = _messages.GetOrAdd(dto.TenantId, _ => new List<WhatsAppMessage>());  
    lock (list)  
    {  
        list.Add(new WhatsAppMessage  
        {  
            Id = Guid.NewGuid().ToString(),  
            From = conn.PhoneNumber,  
            To = dto.To,  
            Text = dto.Text,  
            Type = "text",  
            Timestamp = DateTime.UtcNow,  
            Direction = "out",  
            TenantId = dto.TenantId  
        });  
    }  

    return Ok(new { ok = true

هذا الكود للواتساب كنترولر هل عندك افضل منه
