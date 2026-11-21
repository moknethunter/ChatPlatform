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
