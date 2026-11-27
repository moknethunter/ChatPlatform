using ChatPlatform.Infrastructure.Subscriptions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPlatform.Infrastructure.Adapters.WhatsApp;
using ChatPlatform.Infrastructure.Data;
using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Bots;

public static class BotRuntime
{
    // Match web JSON serialization for saved flow definitions / توافق JSON.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Called only by the durable, signed webhook inbox path. The caller saves the inbound
    // message, execution receipt and outbound rows together, so a failed commit queues nothing.
    public static async Task StageAsync(PlatformDbContext db, WhatsAppConversationEntity conversation,
        WhatsAppMessageEntity inbound, string? text, DateTime now, CancellationToken ct)
    {
        // Only valid owned inbound text can trigger automation / رسالة واردة ضمن المؤسسة.
        if (inbound.Direction != "Inbound" || inbound.MessageType != "text" || string.IsNullOrWhiteSpace(text)
            || inbound.TenantId != conversation.TenantId || inbound.ConversationId != conversation.Id
            || string.IsNullOrWhiteSpace(inbound.Wamid) || conversation.ContactPhone.Length is < 7 or > 15
            || conversation.ContactPhone[0] == '0' || conversation.ContactPhone.Any(c => c is < '0' or > '9')) return;
        // Derive scope from verified conversation / نطاق المحادثة.
        var tenant = conversation.TenantId;
        // Check both staged and saved execution receipts / منع التكرار.
        if (db.AutomationRuns.Local.Any(x => x.InboundMessageId == inbound.Id)
            || await db.AutomationRuns.AnyAsync(x => x.InboundMessageId == inbound.Id, ct)) return;

        // Get one immutable published flow for this tenant / نسخة منشورة للمؤسسة.
        var release = await db.BotReleases.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant, ct);
        // Record execution metadata, not arbitrary raw payload / سجل التنفيذ.
        var run = new AutomationRunEntity { InboundMessageId = inbound.Id, TenantId = tenant,
            ConversationId = conversation.Id, RecipientPhone = conversation.ContactPhone, CreatedAtUtc = now,
            BotId = release?.DraftId, BotVersion = release?.Version };
        // Stage receipt in caller's transaction / حفظ ضمن معاملة المستدعي.
        db.AutomationRuns.Add(run);
        // Reject stale/future input or inactive account/tenant / تحقق النافذة والحساب.
        if (inbound.TimestampUtc > now || now - inbound.TimestampUtc >= TimeSpan.FromHours(24)
            || conversation.LastInboundAtUtc is not DateTime at || at > now || now - at >= TimeSpan.FromHours(24)
            || !await db.WhatsAppTenants.AsNoTracking().AnyAsync(a => a.TenantId == tenant && a.PhoneNumberId == conversation.PhoneNumberId
                && a.IsActive && a.BusinessTokenEncrypted != "", ct)
            || !await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenant && t.IsActive, ct))
        { run.Outcome = "account_or_window_unavailable"; return; }

        // Read tenant/conversation-specific automation state / حالة محادثة معزولة.
        var state=await db.ConversationAutomation.SingleOrDefaultAsync(s=>s.TenantId==tenant && s.ConversationId==conversation.Id,ct);
        // Human mode suppresses bot activity / وضع الموظف.
        if(state?.Mode=="human"){run.Outcome="human_mode";return;}
        // Ignore older messages instead of rewinding state / منع تراجع السياق.
        if(state?.LastInputAtUtc is DateTime prior && inbound.TimestampUtc<prior){run.Outcome="stale_input";return;}
        // Text to stage for outgoing delivery / نص الرد.
        string reply;
        // Minimum delay for repeated automated responses / فترة التهدئة.
        int cooldown;
        // Null means legacy rule or no flow / نتيجة المخطط.
        BotFlow.Evaluation? evaluation=null;
        // Only same activation and unexpired cursor can resume / استئناف صالح.
        var waiting=release!=null && state?.ActivationId==release.ActivationId && state.NextNode!=null && state.ExpiresAtUtc>now;
        // Published release takes precedence / أولوية المنشور.
        if (release != null)
        {
            // A published flow takes exclusive priority, even if it produces no reply.
            // Deserialize saved immutable definition / تعريف النسخة.
            BotFlow.Definition? flow;
            // Parse stored JSON; no remote code execution / قراءة JSON.
            try { flow = JsonSerializer.Deserialize<BotFlow.Definition>(release.DefinitionJson, Json); }
            // Fail closed on invalid saved definition / رفض التعريف التالف.
            catch (JsonException) { run.Outcome = "invalid_release"; return; }
            // Revalidate release before use / تحقق قبل التنفيذ.
            if (BotFlow.Validate(flow) != null) { run.Outcome = "invalid_release"; return; }
            // Ensure cursor exists in current graph / تحقق نقطة الاستئناف.
            var resume=waiting && flow!.Nodes!.Any(n=>n.Id==state!.NextNode)?state!.NextNode:null;
            // Default to no conversation variables / ذاكرة فارغة.
            Dictionary<string,string>? variables = null;
            // Bound stored state before deserialization / تقييد الذاكرة.
            if (resume != null && state!.VariablesJson.Length <= 32768)
                // Restore saved values for current activation / استعادة القيم.
                try { variables = JsonSerializer.Deserialize<Dictionary<string,string>>(state.VariablesJson, Json); }
                // Ignore malformed persisted memory / تجاهل ذاكرة تالفة.
                catch (JsonException) { /* Invalid old memory is ignored. */ }
            // Use the same evaluator as simulator / محرك مشترك.
            evaluation=BotFlow.Evaluate(flow!,text,resume,variables);
            // Combine replies in one text message / دمج الردود.
            reply=string.Join("\n\n",evaluation.Replies);
            // A prompt's answer must not be swallowed by a cooldown on the preceding prompt.
            // Do not suppress a reply to a waiting prompt / استثناء الاستئناف.
            cooldown=resume!=null?0:5;
            // Persist explicit human handoff / تحويل بشري.
            if(evaluation.Handoff)
            {
                // Create missing state with exact tenant/conversation / إنشاء حالة معزولة.
                if(state==null){state=new ConversationAutomationEntity{TenantId=tenant,ConversationId=conversation.Id};db.ConversationAutomation.Add(state);}
                // Clear waiting cursor and variables / مسح سياق البوت.
                state.Mode="human";state.ActivationId=null;state.NextNode=null;state.ExpiresAtUtc=null;state.VariablesJson="{}";
                // Track latest input and update actor / توثيق التوقيت.
                state.LastInputAtUtc=inbound.TimestampUtc;state.UpdatedAtUtc=now;state.UpdatedBy="bot";
                // Cancel pending automated sends transactionally / إلغاء المعلق.
                await HumanHandoff.CancelPendingAsync(db,conversation,now,ct);
                // Record terminal handoff without sending reply / تسجيل النتيجة.
                run.Outcome="human_handoff";return;
            }
        }
        // Legacy rules apply only without a release / القواعد القديمة.
        else
        {
            // Bound ordered tenant-scoped rule lookup / استعلام محدود.
            var rules = await db.AutoReplyRules.AsNoTracking().Where(r => r.IsActive &&
                (r.TenantId == tenant || (tenant == TenantEntity.DefaultTenantId && r.TenantId == "default")))
                .OrderBy(r => r.Priority).ThenBy(r => r.Id).Take(100).ToListAsync(ct);
            // Choose first matching rule by priority / اختيار القاعدة.
            var matched = rules.FirstOrDefault(r => WhatsAppAutoReplyAdapter.Matches(r, text));
            // No match means no queued reply / لا إرسال.
            if (matched == null) { run.Outcome = "no_match"; return; }
            // Record exact matched rule / معرف القاعدة.
            run.RuleId = matched.Id;
            // Substitute current input into legacy template / استبدال النص.
            reply = matched.ReplyText.Trim().Replace("{message}", text.Trim(), StringComparison.OrdinalIgnoreCase)
                .Replace("{text}", text.Trim(), StringComparison.OrdinalIgnoreCase);
            // Bound legacy cooldown / حد التهدئة.
            cooldown = Math.Clamp(matched.CooldownSeconds, 0, 86400);
        }
        // Cap outgoing message size / حد الرد.
        if (reply.Length > 4000) reply = reply[..4000];
        // Compute cooldown cutoff from server time / بداية النافذة.
        var since = now.AddSeconds(-cooldown);
        // Prevent repeated queued replies for tenant/recipient / منع التكرار الزمني.
        if (!string.IsNullOrWhiteSpace(reply) && cooldown > 0 && await db.AutomationRuns.AsNoTracking().AnyAsync(r => r.TenantId == tenant &&
            r.RecipientPhone == conversation.ContactPhone && r.Outcome == "queued" && r.CreatedAtUtc >= since, ct))
        { run.Outcome = "cooldown"; return; }

        // Reserve only when entitlement permits / تحقق الحصة.
        if(!string.IsNullOrWhiteSpace(reply)&&await Entitlements.CapacityAsync(db,tenant,"messages",1,now,ct) is string denied){run.Outcome=denied;return;}
        // Update flow state only for evaluated published flow / تحديث الحالة.
        if(evaluation!=null)
        {
            // Create scoped row if needed / إنشاء صف الحالة.
            if(state==null){state=new ConversationAutomationEntity{TenantId=tenant,ConversationId=conversation.Id};db.ConversationAutomation.Add(state);}
            // Store active release and next cursor / حفظ نقطة الاستئناف.
            state.Mode="bot";state.ActivationId=release!.ActivationId;state.NextNode=evaluation.NextNode;
            // Wait expires after 30 minutes; terminal clears it / صلاحية الانتظار.
            state.ExpiresAtUtc=evaluation.NextNode!=null?now.AddMinutes(30):null;
            // Retain variables only while waiting / حفظ الذاكرة المؤقتة.
            state.VariablesJson=evaluation.NextNode!=null?JsonSerializer.Serialize(evaluation.Variables,Json):"{}";
            // Record input ordering and update metadata / توثيق التحديث.
            state.LastInputAtUtc=inbound.TimestampUtc;state.UpdatedAtUtc=now;state.UpdatedBy="bot";
        }
        // State may advance without outbound message / تقدم بلا إرسال.
        if(string.IsNullOrWhiteSpace(reply)){run.Outcome="no_match";return;}
        // Deterministic tenant+WAMID idempotency key / مفتاح منع التكرار.
        var key = "automation:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenant+"\n"+inbound.Wamid)));
        // Check both pending tracked rows and stored reservations / منع إرسال مكرر.
        if (db.WhatsAppOutbox.Local.Any(x => x.TenantId == tenant && x.IdempotencyKey == key)
            || await db.WhatsAppOutbox.AnyAsync(x => x.TenantId == tenant && x.IdempotencyKey == key, ct))
        { run.Outcome = "duplicate"; return; }
        // Serialize supported text payload, never direct HTTP send / تجهيز حمولة.
        var payload = JsonSerializer.Serialize(new { messaging_product = "whatsapp", recipient_type = "individual",
            to = conversation.ContactPhone, type = "text", text = new { body = reply, preview_url = false } });
        // Stage visible queued message in owned conversation / رسالة معلقة.
        var outgoing = new WhatsAppMessageEntity { TenantId = tenant, ConversationId = conversation.Id,
            Direction = "Outbound", MessageType = "text", ContentJson = payload, TextPreview = reply.Length > 200 ? reply[..200] : reply,
            Status = "Queued", TimestampUtc = now, CreatedAtUtc = now };
        // Persist with inbound and receipt in caller transaction / حفظ ذري.
        db.WhatsAppMessages.Add(outgoing);
        // Reserve durable worker job with correct account and recipient / طابور دائم.
        db.WhatsAppOutbox.Add(new WhatsAppOutboxEntity { TenantId = tenant, PhoneNumberId = conversation.PhoneNumberId,
            MessageId = outgoing.Id, IdempotencyKey = key, RecipientPhone = conversation.ContactPhone, MessageType = "text",
            PayloadJson = payload, CreatedAtUtc = now, UpdatedAtUtc = now, NextAttemptAtUtc = now, Status = "Pending" });
        // Link receipt to queued message; not delivery confirmation / ربط سجل التنفيذ.
        run.OutboundMessageId = outgoing.Id; run.Outcome = "queued";
    }
}
