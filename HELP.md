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
- Login at /login.html (HTTPS required by the secure cookie); /api/auth/me returns identity. Cookie lifetime 30 minutes; logging out clears the browser cookie, but a copied cookie can remain valid until expiry or account config change. Do not deploy until durable server-side session revocation and account provisioning are implemented.
- Writes require X-ChatPlatform-CSRF: 1. Admin can manage local templates and auto-reply rules; Agent reads only. Conversations and account listings are scoped by the authenticated tenant.
- Unreviewed API endpoints and SignalR are intentionally denied (401/403). No real sending, onboarding, connector administration or registration has been enabled by this change. Existing UI flows require further integration and testing before release.
- CI uses synthetic credentials and a disposable database. Run tests/security_smoke.py against the isolated service configured by the workflow; never run against production.
