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

- A receipt worker now maps signed stored delivery events to the exact outbound WAMID/tenant/account/recipient and advances accepted → delivered → read, without downgrading on late failure or sent notifications. Outbox status Sent continues to mean accepted by Meta; campaign messageStates and reports.show the latest message status separately. Unmatched receipts stay pending for possible delayed send commit; retention/cleanup remains pending.

## Platform operator, plans and manually managed subscriptions
- Bootstrap SuperAdmin exclusively through server configuration Security__Accounts__N__Username, PasswordHash, Role=SuperAdmin, TenantId=__platform_owner__. No tenant record is required for that reserved scope; tenant endpoints still require Admin/Agent. No default password, impersonation, cross-tenant message reads or payment provider. SuperAdmin can create immutable plan revisions, provision a tenant and its initial managed Admin, renew/suspend subscriptions, suspend/reactivate tenant and reset managed Admin password; all administrative writes record metadata in an audit log, never passwords. Existing configured tenant Admin accounts remain supported.
- Plans specify active managed agent and WhatsApp account counts, saved bot drafts, knowledge articles, UTC calendar-month outgoing queue reservations (including failed/cancelled reservations and campaign scheduled date), and marketing/owner-model preview features. Queue and creation checks occur inside SQLite transactions; idempotent replays do not count twice. Expired/suspended subscription blocks new gated operations and dispatch; tenant can read existing data and manage cancellation. Existing tenants with no assigned plan retain previous behavior until migrated. Plan reductions do not delete resources or cancel prior reservations automatically. Price is stored in minor units for informational manual subscriptions only; no automatic charges, invoicing or payment collection. Full billing, browser checks, and production owner MFA remain pending.

- /platform-admin.html is a separate operator workspace (tenant list, plan creation, subscription assignment, admin password reset, audit). /subscription.html is tenant-admin usage and expiration view. No prices are charged. All user-visible controls call role-protected APIs. CI provisions a synthetic owner and tenant; tests owner/tenant boundary, immutable plan reservations, suspension/revocation and quotas. Actual browser checks and real production owner provisioning remain pending.

- Explicit tenant or subscription suspension now cancels pending outbound reservations in the same write transaction, without refunding quota; messages already in-flight cannot be recalled. Reactivation does not resurrect cancelled rows. The dispatch worker rechecks account/tenant/subscription after remote approval checks. Tests cover one-slot quota with idempotent replay and cancellation of a synthetic future reservation. SuperAdmin has no direct tenant read policy; administrative password-reset authority is privileged and must be protected in production (MFA and out-of-band recovery still required).

## Verified Fly.io deployment
- Production deployment stays manual via GitHub Actions → Deploy ChatPlatform to Fly.io → Run workflow (main). An explicitly approved merge changing the deployment workflow with the marker `[deploy-fly-approved]` also starts deployment. Ordinary main commits do not deploy automatically.
- The deployment first runs the reusable isolated CI; then it builds the merged revision, retaining the existing app and /data mount, and checks both public health endpoints. `/health` includes the exact revision, `/ready` checks database connectivity. A stale revision or non-200 readiness fails verification. Requires a valid existing `FLY_API_TOKEN` secret and operational Fly account; no secrets are printed.
- Read-only external observation: `python3 scripts/check_deployment.py`. To verify an expected deployed commit: `python3 scripts/check_deployment.py --expected-revision <commit-sha>`. PASS proves revision and basic DB connectivity, not real WhatsApp delivery.

## Database recovery and container checks
- Run these commands from a trusted Linux host with Python 3 and direct file access. Source is the real SQLite database, not a downloaded web page. A Fly mounted database is normally `/data/platform.db`; on Compose it is `/app/data/platform.db` inside the container. The Python recovery tool is a repository operator tool, not a web endpoint or bundled Python runtime.
- Create a private backup directory and a new consistent snapshot:
  ```bash
  mkdir -m 700 -p "$HOME/chatplatform-backups"
  python3 scripts/database_recovery.py backup --source /data/platform.db --destination "$HOME/chatplatform-backups/platform-$(date -u +%Y%m%d-%H%M%S).db"
  ```
  Output: PASS and SHA256. Uses SQLite online backup (including committed WAL), validates integrity and expected tables, creates mode-0600 file, refuses overwrite. Retain encrypted copies off-server and protect keys and customer data. Do not upload snapshots to GitHub or CI artifacts. The backup deadline bounds SQLite page-copy progress; integrity checks may take longer on large databases.
- Restore into a **new, offline** private destination, not the live mounted volume:
  ```bash
  python3 scripts/database_recovery.py restore --source /secure/known-backup.db --destination /secure/restored.db
  ```
  Output: PASS, SHA256, and reminder to reconcile queues. Verify the backup hash against the recorded trusted value. Restore revokes database sessions but preserves data-protection keys and other records. External Meta media, configuration/secret values supplied outside SQLite, and Fly metadata are not backed up. Before replacing a production database: stop all workers, take a fresh snapshot, reconcile pending and ambiguous delivery/receipt/campaign records against Meta to avoid replay, restore with networking disabled first, and verify ownership and key decryption. No live replacement or automatic replay is implemented.
- CI executes `python3 tests/backup_smoke.py` against the synthetic integration database; checks a WAL snapshot, restoration, session revocation, corruption detection and refusal to overwrite. Docker now has a built-in readiness healthcheck; CI builds the real image and observes its health status with networking disabled.
- Compose uses `CHATPLATFORM_IMAGE` (default chat66d:latest). To test an already published immutable tag set `CHATPLATFORM_IMAGE=ghcr.io/moknethunter/chat66d:sha-<revision>` then use `docker compose config --quiet` to validate. Starting or updating a production container still requires operator approval and configured secrets.

## WhatsApp legacy account recovery (candidate; review before merge)
- Migration preserves valid account dates in both timestamp column layouts, keeps tenant and phone ownership, and never copies a revoked legacy token into an inactive account. Run the initializer twice on a synthetic legacy schema to test idempotency. Do not migrate the live database without first verifying an encrypted, restorable backup. Unknown or conflicting ownership requires manual review, not reassignment.
- In WhatsApp account settings, an Admin can enter missing WABA metadata when updating a token. The server verifies exact phone membership in the WABA, refuses a different established WABA, and rechecks the account after verification. Disconnect clears both encrypted credential columns. Explicit reconnect requires verification and an available account quota. Tenant list and account list are authenticated; legacy unscoped send/list/write routes remain denied.
- In WhatsApp account settings, choose **التحقق لدى Meta** as the active account's tenant Admin. The account needs a saved token and WABA ID. A successful result confirms that the stored token can read the phone's membership in its WABA. This check sends no message and returns no token. Webhook delivery and receipts require a separate live test.
- Read-only Fly observation workflow is restricted to development/live-audit-20261122. It prints public health/revision, readiness and HTTP status codes only, including negative unsigned webhook requests. No authenticated customer records, secrets or delivery tests. Actual signed Meta traffic and production backup/restore remain separate operator-approved steps.


## Embedded Signup candidate (disabled by default)
- Keep `Meta__EmbeddedSignupEnabled=false` on production and on the existing Meta test-number deployment. This candidate is not a complete commercial onboarding flow.
- Server-only settings for a future isolated trial: `Meta__AppId`, `Meta__EmbeddedSignupConfigId`, `Meta__AppSecret`. Never commit secrets or expose them to the browser. The app and config IDs are returned only after an authenticated tenant administrator starts an attempt.
- POST `/api/whatsapp/embedded-signup/start` issues a five-minute, single-use correlation value bound to the authenticated session, tenant, app and configuration. POST `/api/whatsapp/embedded-signup/complete` accepts state, code, wabaId and phoneNumberId. The server exchanges the code, verifies phone membership and confirms that the configured app is already subscribed. It then uses the same account creation, quota and encryption checks as manual linking. It does not register a phone or subscribe an app. On failure, restart the attempt; do not reuse the code.
- Correlation is process-local and is lost on restart; horizontal deployment requires a shared atomic store. A candidate browser SDK flow now loads only after an explicit prepare action, accepts only known Facebook HTTPS origins and FINISH events with both IDs, correlates the SDK callback and event in either order and submits once. The actual SDK behavior on Meta, callback/source correlation in real browser sessions, Meta review/configuration, subscription repair, phone registration, and end-to-end validation remain release blockers. Existing manual account linking remains available. Do not enable the candidate before these blockers are addressed.


## CRM activity and marketing segments
- Open a customer profile to see the latest 50 conversations and 30 message summaries in the current tenant. Administrators can see recorded marketing permission per active WhatsApp account; missing permission is not consent.
- In Marketing > customer lists, choose the sending account, customer stage and optional tag substring, then preview. Only customers with recorded consent for that exact account appear as selectable items. The preview displays counts and at most 100 eligible contacts sorted by phone; narrow filters if truncated. Select recipients, transfer them to a new list draft, name it and save explicitly. Preview does not create a list, grant consent, schedule or send messages.
- A saved list is a snapshot, not a dynamic audience. Campaign scheduling and the send worker still enforce consent for the selected template's account; revoked or absent consent excludes recipients. Existing campaign enablement and Meta approval requirements remain unchanged.


## Visual bot designer enhancements
- Move a node by dragging it, or select a source node and choose the next/yes or no link action, then select a destination node. The properties dropdown remains available. Save and simulate to detect missing links, cycles and unreachable nodes. Use automatic layout for a compact grid or return to the start node.
- Text conditions support contains (default for existing flows), equals (case-insensitive, trim both ends), and starts_with (case-insensitive, ignore leading input whitespace). Simulation highlights visited nodes; multi-turn simulation highlights the union of visited nodes. These modes use the same evaluator as runtime. Saved drafts never replace a published snapshot until explicitly published.
- The designer still permits a maximum of 40 nodes and a single published flow per tenant. Arbitrary variables, general-purpose persistent memory, external actions, and real mobile browser interaction testing remain separate work.
