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
