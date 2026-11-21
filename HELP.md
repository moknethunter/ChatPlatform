# دليل أوامر ChatPlatform

نفّذ الأوامر من جذر المشروع. تحتاج إلى Git و.NET SDK 8 وDocker Compose v2 حسب الأمر.

| الأمر | الوظيفة / النتيجة المتوقعة |
|---|---|
| `git status` | معرفة الفرع والتغييرات المحلية قبل العمل. |
| `git fetch origin` | تحديث مراجع الفروع دون تعديل الملفات المحلية. |
| `git switch main` | الانتقال للفرع الرئيسي؛ لا تستخدمه مع تغييرات غير محفوظة. |
| `git pull --ff-only` | تحديث الفرع دون إنشاء دمج غير مقصود. |
| `git switch -c development/my-task` | إنشاء فرع معزول لمهمة جديدة. |
| `dotnet --info` | التحقق من توفر SDK وبيئة التشغيل. |
| `dotnet restore ChatPlatform.csproj` | استعادة الحزم؛ يجب أن ينتهي بنجاح. |
| `dotnet build ChatPlatform.csproj -c Release --no-restore` | بناء المشروع بعد الاستعادة؛ يجب ألا توجد أخطاء. |
| `dotnet list ChatPlatform.csproj package --vulnerable --include-transitive` | تدقيق ثغرات الحزم المباشرة وغير المباشرة؛ راجع النتائج ولا تتجاهلها. |
| `docker compose config --quiet` | التحقق من بنية Compose دون تشغيل حاويات. |
| `docker build -t chatplatform:local .` | بناء صورة محلية دون نشرها. |
| `git diff` | مراجعة التغييرات غير المرحّلة. |
| `git diff --check` | فحص مشاكل المسافات في التغييرات. |

## تشغيل محلي معزول (Bash / Linux)

```bash
# إنشاء مجلد مؤقت منفصل عن الإنتاج
export CHATPLATFORM_DATA_DIR="$(mktemp -d)"
# توجيه SQLite إلى المجلد المؤقت
export ConnectionStrings__DefaultConnection="Data Source=$CHATPLATFORM_DATA_DIR/platform.db"
# اختيار عنوان محلي فقط لمنع الوصول الخارجي
export ASPNETCORE_URLS=http://127.0.0.1:5080
# محاكاة وضع الإنتاج لأغراض اختبار الأمان
export ASPNETCORE_ENVIRONMENT=Production
# بيانات اختبار وهمية فقط (ليست توكنات Meta حقيقية)
export WHATSAPP_VERIFY_TOKEN=ci-only-test-token
export WHATSAPP_APP_SECRET=ci-only-test-secret
# تشغيل التطبيق محلياً
dotnet run --project ChatPlatform.csproj --no-launch-profile
```

من طرفية أخرى:
```bash
# اختبار جاهزية الخادم: المتوقع HTTP 200
curl -i http://127.0.0.1:5080/health
# اختبار منع مسارات البذر في الإنتاج: المتوقع HTTP 404 بعد الإصلاح
curl -i http://127.0.0.1:5080/dev/seed/list
# اختبار رفض webhook غير موقع: المتوقع HTTP 401
curl -i -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:5080/webhook
```

أوقف الخادم بـ Ctrl+C. لا تستخدم بيانات اعتماد حقيقية أو قاعدة إنتاج في هذه الخطوات. راجع نتائج GitHub Actions قبل الدمج. الدمج إلى main يشغّل إجراءات النشر الحالية تلقائياً.

## فحص جاهزية التخزين
```bash
# التحقق من اتصال قاعدة البيانات؛ 200 عند الاتصال، 503 عند تعذره
curl -i http://127.0.0.1:5080/ready
```
اختبار منع نقل ملكية حساب واتساب بين المؤسسات ينفذ تلقائياً في CI باستخدام بيانات وهمية. لا تختبره على حساب حقيقي.

## اختبار الإرسال والرد الآلي المعزول
```bash
# اختبار الإرسال والرد الآلي عبر عامل Outbox وواجهة Graph وهمية، بلا اتصال خارجي
 dotnet run --project tests/MessagingSmoke/MessagingSmoke.csproj -c Release
```
النتيجة المتوقعة: PASS ورسالتان بحالة Sent في قاعدة الاختبار فقط. لا يدل ذلك على تسليم رسائل حقيقية عبر Meta.

## تشخيص حسابات واتساب في الإنتاج (قراءة فقط)
```bash
# التحقق من نشر نسخة سليمة وحالة الآلات
fly status -a chatplatform
# قراءة سجلات الخادم، وابحث عن TraceId المطابق للخطأ؛ لا تشارك الأسرار
fly logs -a chatplatform --no-tail
# فحص اتصال قاعدة البيانات
curl -fsS https://chatplatform.fly.dev/ready
``
من لوحة Fly.io افتح Monitoring ثم Live Logs قبل إعادة طلب accounts. لا تحذف مجلد /data أو قاعدة البيانات. وجود configured=true يثبت وجود الإعدادات فقط، وليس صلاحية التوكن أو اشتراك messages لدى Meta. اختبار CI يحاكي قاعدة قديمة وتوكنات وهمية، ويتحقق من القراءة والتشفير دون إرسال حقيقي.

## Trusted reverse proxies and development CI
- Configure `Security__TrustedProxies__0` (and subsequent indices) only with verified immediate proxy IP addresses. Loopback is trusted by default. Verify Fly.io proxy topology before deploying this change; do not clear the trust lists or enable unrestricted forwarded headers.
- Development branch pushes run isolated verification, including a dependency vulnerability report. Inspect the report and actual test results before merging; the report alone does not enforce a vulnerability gate.
- Authentication and cross-tenant authorization remain release blockers. Existing anonymous smoke checks must be revised together with authentication.

## Authentication candidate (development branch only)
- Set `Security__Accounts__0__Username`, `Security__Accounts__0__TenantId`, `Security__Accounts__0__Role` (Admin/Agent) and `Security__Accounts__0__PasswordHash` through secret configuration. Use ASP.NET Identity PasswordHasher V3 output; never commit a password/hash. Each username must be unique and the referenced tenant active.
- Login at /login.html (HTTPS required by the secure cookie); /api/auth/me returns identity. Cookie lifetime 30 minutes; sessions are checked in SQLite and logout deletes the server-side session. Account provisioning is still operator-managed and must be configured before production.
- Writes require X-ChatPlatform-CSRF: 1. Admin can manage local templates and auto-reply rules; Agent reads only. Conversations and account listings are scoped by the authenticated tenant.
- Unreviewed API endpoints and SignalR are intentionally denied (401/403). Text sending uses a tenant-scoped durable message/outbox transaction and a request idempotency key; approved-template sending, onboarding, connector administration and account registration remain disabled until reviewed. Existing UI flows require further integration and testing before release.
- CI uses synthetic credentials and a disposable database. Run tests/security_smoke.py against the isolated service configured by the workflow; never run against production.

- Template schema migration adds missing MetaTemplateId and UpdatedAtUtc without deleting rows. Back up the production volume and test restore before applying migrations. Security smoke tests use fake accounts and assert foreign account/invalid session requests never reach outbound delivery.

## Tenant account and template sending candidate
- Manual account creation validates numeric Meta IDs and confirms the phone belongs to the specified WABA using the supplied token before storing it. Only an organization Admin may create or rotate credentials. Deactivation preserves ownership and removes the token. Legacy accounts without a WABA ID need a reviewed migration; OAuth onboarding remains disabled.
- Template sends use the same tenant-scoped durable outbox as text sends. Meta decides whether the template is approved; queued does not mean sent. The sender remains responsible for recipient opt-in and Meta policy compliance. Negative HTTP tests do not contact Meta; positive account verification and template delivery need mocked integration tests.
- The first WABA phone list page is checked; missing numbers are rejected rather than following untrusted paging URLs. Connector endpoints, SignalR and embedded signup remain disabled pending review.

- MessagingSmoke now invokes the real text/template controller with a synthetic tenant identity and repeats the idempotency keys; the worker must send all four expected messages only to a mocked Graph handler. The HTTP smoke suite separately checks authentication and tenant denials. Diagnostic null values mean unknown, not disconnected or verified.

## Phase 4: team administration
- Open /team.html while signed in as a configured tenant Admin. Create Agent users with lowercase unique usernames and passwords of at least 14 characters. Do not share passwords through GitHub, logs or chat. The server hashes passwords and never returns them.
- Agents may only use the existing tenant-scoped Agent policies. Tenant administrators cannot create another administrator, move a user to another tenant, or manage configuration identities. Resetting passwords or changing activity revokes all managed-agent sessions. Configuration identities remain reserved.
- The isolated HTTP smoke suite tests agent creation, cross-tenant access denial, role escalation attempts, password reset and session revocation. UI browser verification and a secure initial-password delivery/change workflow remain pending.

## CRM and shared inbox
- /contacts.html lists profiles by tenant; new conversation inserts create a profile automatically. Startup backfills existing conversation contacts without overwriting edited profiles. A contact is uniquely keyed by (TenantId, Phone). Set stage to new, lead, customer or inactive; notes are shared within the tenant. No bulk import/export or deletion is implemented.
- /inbox.html is now a shared inbox with search/filter/pagination, text replies, manual refresh, status, unread reset, tags and administrator-only assignment to active managed agents. Assignment is routing metadata, not an agent-only access boundary: all authenticated team members may read their own tenant conversations. No real-time transport or attachment rendering yet.
- CI uses synthetic contacts and checks same-phone isolation, cross-tenant writes, role restrictions, assignment/status and unread updates. Browser UI checks remain pending.

## Media candidate and automatic inbox updates
- Inbox supports selected JPEG/PNG, MP4, PDF and Ogg/Opus or MP3 attachments up to 5 MiB. Files are uploaded to Meta for the selected conversation; the returned short-lived encrypted ticket is bound to tenant and conversation. Sending uses the durable outbox and a stable idempotency key (within a retry of the same ticket). No arbitrary links or raw media IDs are accepted from clients. Meta validates formats/codecs. Media uploads are not persisted locally and may leave unused Meta uploads if a send is abandoned.
- /api/media/{messageId} fetches the stored media ID with the owning account token, bounds response size, forbids redirects and allows only HTTPS lookaside.fbsbx.com. Expired or unavailable media returns an error; PDF is download-only. No permanent media archive or antivirus scanning. File signatures are basic checks, not a malware guarantee. Limit concurrent use and verify live Meta integration in a separate environment before release.
- The inbox refreshes every five seconds while visible, without a SignalR connection. Every refresh verifies session and tenant again. True push and browser recording remain pending. No real Meta requests in CI; negative HTTP tests and media signature/host checks are included.

- The MessagingSmoke test now exercises upload, retrieval, ticket ownership and idempotent media delivery against a fake HTTP handler, with no real network requests. The optional browser microphone recorder is enabled only if Ogg/Opus MediaRecorder is supported; otherwise users can attach an existing MP3/Ogg file. Recordings are limited to 120 seconds and 5 MiB, and require explicit send.

## Live inbox and bot drafts
- /api/live/inbox streams tenant-specific revision numbers only over SSE; SQLite triggers track messages, conversation and CRM mutations. Sessions are revalidated every second, streams close after 25 seconds and permit two simultaneous connections per user. Browser reconnects and uses five-second polling as fallback. No client-supplied tenant or group names; legacy SignalR remains denied. Proxy streaming and load tests pending.
- /bots.html is an Admin-only draft editor with draggable nodes, coordinate inputs, links via selectors, save/version conflict protection and a side-effect-free simulator. Supported nodes: start, contains, text reply, end. Graphs are bounded to 40 nodes, reject cycles or unreachable nodes and require valid branches. Drafts are not published or executed on incoming messages. No AI or external requests in simulation. CI verifies tenant boundaries, invalid graphs and version conflicts. Real-browser UX checks remain pending.

- The HTTP smoke suite explicitly opens a live stream, logs out that session and expects a session-expired event and 401 on reconnect. The bot validation tests also reject missing edges and unreachable nodes.

## Published bot runtime
- /bots.html: save and simulate a draft, then publish the exact saved version. One published immutable snapshot per tenant; a new publication replaces it. Stop is bound to the activation ID. A published bot exclusively handles new inbound text even if no condition matches. Without a release, the highest-priority matching legacy rule is evaluated durably. A bot combines up to three reply nodes into one message (<=4000 chars), with a five-second per-recipient cooldown; legacy cooldown is preserved. Legacy matching scans up to 100 active rules, regex timeout 50ms.
- Signed inbound processing commits the conversation/message, automation execution receipt and outbox reply in one SaveChanges transaction. One receipt per inbound message, unique tenant/wamid outbox key. The old event adapter skips automationHandled notifications. No real network in CI; fake encrypted token prevents sending in HTTP tests, and a separate fake Graph handler verifies successful delivery. Queue deduplication is not a guarantee of exactly-once delivery at Meta after a network failure. Stop affects future messages; already queued replies may still send. Worker rechecks the 24-hour window before automatic delivery.
- This is a stateless per-message flow engine, not a multi-turn conversation state machine. The execution log shows queued/no_match/cooldown/account unavailable; queue status does not imply delivery. Browser and production integration tests remain required.

- Automation reliability: stale signed message timestamps do not extend the customer-service window. The inbox uses a database transaction for ownership, bot decision and atomic queueing. Automated sends stop on ambiguous delivery (HTTP 5xx, timeout, network error, missing Meta message ID or expired processing lease); status is DeliveryUnknown, requiring operator reconciliation. Explicit HTTP 429 rejection can be retried. This favors avoiding duplicates over delivery at all costs; Meta exact-once delivery cannot be promised.

- Mocked Graph tests assert a 500 response produces exactly one automated attempt, and an abandoned processing lease produces no further network request, both marked DeliveryUnknown.

## Merge and release separation
- Merging into main runs CI only. Repository Fly.io and GHCR release workflows now run only by explicit workflow_dispatch from main. This prevents a code merge from automatically deploying authentication changes or overwriting production images. Do not dispatch before explicit production approval, verified administrator account provisioning, proxy/HTTPS configuration and tested database backup/restore. External hosting auto-deploy settings are outside this repository workflow control; verify separately before release.

## Owner model and knowledge
- /api/knowledge implements tenant-scoped, Admin-only articles (500 max, drafts excluded from search). Local lexical ranking requires no AI or embedding service. /api/knowledge/preview is a manual test only; no automatic WhatsApp AI replies. Default is disabled. Operator config: OwnModel__Enabled=true, OwnModel__Endpoint=https://<owner-host>/inference, OwnModel__Token=<private service token> (never commit); plain HTTP permitted only on loopback. Endpoint configured only server-side; redirects disabled, one concurrent preview per user, response <=32 KiB, deadline 30s. Model receives question and at most four published article excerpts, no account tokens or automatic conversation history. No external provider fallback. Model connection is not a health claim merely because configured.
- The owner endpoint accepts POST JSON {protocol:"chatplatform.own-model.v1",question,locale,knowledge:[{id,title,body}]} and returns {answer:"..."} with <=4000 characters. This is a project-owned integration contract, not an existing model. CI checks disabled mode and article isolation; live model tests remain pending.
- Main CI now invokes the same isolated verification workflow as development rather than attempting to write /data on a runner.

- /index.html is now an authenticated dashboard with real tenant-scoped counts, links and recent conversations (daily boundary UTC). /knowledge.html provides article editing, publication, offline search and manual owner-model preview. Shared RTL design tokens/navigation apply to the dashboard, inbox, CRM, team and bots. No browser screenshot or device test has yet been completed.

## Multi-turn automation and human service
- wait nodes emit a prompt, then save the next node per tenant/conversation/activation for 30 minutes. The next text is evaluated at that node, not at the beginning. New publication or expiry restarts the flow. Handoff is terminal without sending a bot reply (earlier replies in the same turn are discarded); it persists human mode even across publication changes. No AI inference or external calls in flow evaluation. Up to 12 reply/wait nodes, total saved text <=4000 characters, 40 graph nodes maximum. Multi-message simulator is side-effect free.
- Inbox: filter for human service, take over or return to bot (starts from beginning). All authenticated team members may operate their own tenant conversations; only Admin assigns a managed employee. Takeover cancels pending automated sends in the same DB transaction and the worker rechecks human mode before dispatch. A send already in flight cannot be recalled. Human mode must be explicitly released. State records cursor/timing, not secret values or AI memory. No external model call or subscription introduced.

- Shared RTL header now covers all authenticated workspace pages including WhatsApp, templates, auto-reply, settings and test tools. Login and public pages inherit shared typography/palette; no public legal content changed. Legacy /autoreply.html redirects to the existing /autoteplay.html. Run `python3 tests/check_web_assets.py` from the repository root with Node installed: expects PASS for inline/external JS syntax, unique IDs and local static links. Visual browser checks and actual phone testing still required.

- Mobile inbox switches between the conversation list and selected conversation with an explicit back button. A synthetic future-scheduled outbox row tests cancellation and message-state synchronization during human takeover.

## Marketing campaigns (candidate)
- Campaigns__Enabled is false by default. Admins record explicit account-specific marketing consent with evidence, manage named lists of existing CRM contacts (up to 100 each), and schedule up to 300 recipients per tenant per scheduled UTC day. Only opted-in contacts are queued, spaced five seconds within each campaign; no global throughput guarantee. The selected local MARKETING template must be simple text-only with no variables; successful scheduling is not Meta approval. The worker verifies the actual approved MARKETING template body/language on the owning WABA immediately before dispatch, fails closed on network/approval errors, and rechecks consent and cancellation. No redirects or external paging. No live Meta test yet. STOP/unsubscribe/Arabic supported exact opt-out phrases revoke consent from signed inbound messages and suppress bot replies to that opt-out. No automatic consent from inbound messages. Already dispatched messages cannot be recalled. Ambiguous deliveries are not automatically resent. Reports show outbox states, not confirmed read/delivery receipts. Direct manual template sends remain a separate workflow, not campaign consent enforcement; operator must comply with Meta rules and applicable laws.

- /marketing.html provides Admin-only consent forms, editable named CRM lists, campaign scheduling and cancellation. /reports.html shows bounded 1–30 day UTC traffic, outbox states, latest stored outgoing message status, bot outcomes and current account/recipient consent counts. Neither queued nor accepted by Meta is described as delivered/read. Scheduled messages are counted at record creation time. No AI dependencies; direct browser and actual Meta verification still pending.
