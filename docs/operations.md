# دليل التشغيل والإصدار

## التشغيل والاختبار المحلي

تحتاج إلى Git و.NET SDK 8. يحتاج فحص الواجهة إلى Python 3 وNode.js، ويحتاج اختبار الصورة إلى Docker وCompose v2. ابدأ من جذر المستودع باتباع [أوامر القاعدة المعزولة](../HELP.md). يستخدم التطبيق SQLite ومجلد بيانات مستقلًا. افتح منفذ loopback فقط في اختبار HTTP المحلي؛ لاختبار جلسة متصفح عادية، اعرض النسخة المعزولة عبر HTTPS موثوق لأن الكوكي يحمل `Secure`.

| الأمر | يستخدم عندما | نتيجة متوقعة |
|---|---|---|
| `dotnet restore ChatPlatform.csproj` | قبل البناء على جهاز التطوير | استعادة الحزم. |
| `dotnet build ChatPlatform.csproj -c Release --no-restore` | بعد الاستعادة | بناء ناجح. |
| `python3 tests/check_web_assets.py` | بعد تعديل الواجهات، مع توفر Node | فحص JavaScript ومعرّفات HTML والروابط الثابتة الداخلية. |
| `dotnet run --project tests/MessagingSmoke/MessagingSmoke.csproj -c Release` | اختبار تكامل محلي | نتائج PASS للترحيل والرموز والوسائط والإرسال الوهمي. |
| `dotnet list ChatPlatform.csproj package --vulnerable --include-transitive` | فحص اعتماديات الإصدار | تقرير من مصادر NuGet المتاحة. |
| `docker compose config --quiet` | التأكد من صحة تعريف Compose | خروج ناجح. |
| `python3 scripts/check_deployment.py --help` | مراجعة خيارات فحص الإصدار | خيارات العنوان وSHA المتوقع والمحاولات. |
| `python3 scripts/database_recovery.py --help` | مراجعة خيارات النسخ والاستعادة | أوامر `backup` و`restore`. |

يُشغّل سير CI قاعدة مؤقتة وأسماء وحسابات مصطنعة. يعطي [سير التحقق](../.github/workflows/isolated-verification.yml) إعدادات تشغيل اختبارات HTTP ونسخ SQLite. بناء Docker ينتهي بتشغيل الحاوية في شبكة معطلة ومراقبة حالتها. نتائج CI تقيس ذلك الإصدار المعزول؛ اختبار Meta الفعلي والتحقق البصري على الأجهزة عمليتان مستقلتان.

## الإعدادات

اضبط الأسرار في إعدادات البيئة أو مخزن الأسرار على المضيف. يمكن تعيين حقول ASP.NET المركبة باستخدام `__`.

| المفاتيح | الاستخدام |
|---|---|
| `CHATPLATFORM_DATA_DIR`، `ConnectionStrings__DefaultConnection` | مجلد SQLite ومسار ملفها. Fly يستخدم `/data/platform.db`، وCompose يستخدم `/app/data/platform.db` داخل الحاوية. |
| `ASPNETCORE_URLS`، `ASPNETCORE_ENVIRONMENT` | عنوان الاستماع وبيئة التشغيل. المنفذ الداخلي في Fly هو 8080. |
| `CHATPLATFORM_REVISION` | الإصدار الظاهر في `/health`. يُمرّر إلى صورة Docker عبر `APP_REVISION`. |
| `WHATSAPP_VERIFY_TOKEN` أو `WhatsApp__VerifyToken` | قيمة التحقق المتفق عليها مع Meta عند GET. |
| `WHATSAPP_APP_SECRET` أو `WhatsApp__AppSecret` | سر HMAC للتحقق من POST. ملف Compose يقرأه من المتغير الخارجي `WHATSAPP_APPSECRET`. |
| `WHATSAPP_PHONE_NUMBER_ID` أو `WhatsApp__PhoneNumberId` | رقم Meta المهيأ للربط الأولي. |
| `WHATSAPP_TOKEN` أو `WhatsApp__AccessToken` | رمز وصول الحساب المهيأ عند التهيئة الأولية إذا طابقت المؤسسة النشطة وكان رمز الحساب فارغًا. ملف Compose يقرأه من `WHATSAPP_ACCESS_TOKEN`. |
| `WHATSAPP_TENANT_ID`، `WHATSAPP_WABA_ID`، `WHATSAPP_APP_ID`، `WHATSAPP_BUSINESS_NAME`، `WHATSAPP_DISPLAY_PHONE_NUMBER` | بيانات الربط الأولي الاختيارية للمؤسسة ورقمها. رمز الحساب المُدار يضبط من واجهة واتساب. |
| `WHATSAPP_API_VERSION` أو `WhatsApp__GraphApiVersion` | نسخة Graph المهيأة في التطبيق. مهام اختبار الخادم تستخدم معالج Graph مصطنعًا. |
| `Security__TrustedProxies__0` وما بعده، `Cors__AllowedOrigins__0` وما بعده | تكوين proxies الموثوقة والأصول المسموحة. تحقق من تمرير HTTPS الفعلي وطبّق عناوين proxies التي تديرها. |
| `Campaigns__Enabled` | مفتاح إتاحة الإرسال التسويقي؛ الافتراضي تعطيل. تراعي الجدولة حالة الاشتراك والباقة. |
| `OwnModel__Enabled`، `OwnModel__Endpoint`، `OwnModel__Token` | المعاينة اليدوية للنموذج الخاص. عنوان HTTPS أو loopback محلي، ورمز خدمة خاص. |
| `CHATPLATFORM_IMAGE` | اسم الصورة التي يشغّلها Compose. استخدم وسم SHA محددًا حين تختبر نسخة للنشر. |
| `FLY_API_TOKEN` | سر GitHub Actions الذي يستخدمه سير Fly للوصول إلى التطبيق. |

### الحسابات والصلاحيات

يُهيّأ مدير المنصة في إعدادات الخادم ضمن `Security__Accounts__N__Username` و`Security__Accounts__N__PasswordHash` و`Security__Accounts__N__Role=SuperAdmin` و`Security__Accounts__N__TenantId=__platform_owner__`، حيث `N` هو رقم الخانة. استخدم تجزئة ASP.NET Identity V3؛ كلمة المرور الخام والتجزئة تُحفظان في قنوات إدارة الأسرار المناسبة. لا يوجد تسجيل أولي عام. يدير مدير المنصة إنشاء المؤسسة ومديرها الأول، ويدير مدير المؤسسة الموظفين.

## النسخ الاحتياطي والاستعادة

تهيئة التطبيق قادرة على ترحيل قاعدة الإنتاج عند إقلاع الإصدار. جهّز نسخة حديثة ومتحققًا منها قبل النشر. أداة Python تحتاج وصولًا مباشرًا إلى ملف SQLite من مضيف موثوق وإلى دليل خاص للنسخة. تستخدم SQLite online backup وتلتقط البيانات الملتزم بها في WAL. لا تنقل قاعدة الإنتاج أو مفاتيحها إلى GitHub.

1. اتبع [أوامر النسخ والاستعادة](../HELP.md#database-recovery-and-container-checks) وخزّن النسخة مشفرة في موقع خاص.
2. تحقّق من نتيجة سلامة SQLite وSHA256 وسجلهما. اختبر الاستعادة إلى ملف جديد خارج الخدمة، ثم تحقّق من البيانات ومفاتيح تشفير رموز واتساب.
3. تلغي الاستعادة جلسات قاعدة البيانات المعدّة للاستخدام وتحتفظ بمفاتيح الحماية. افحص سجلات Meta والطوابير المعلّقة وحالات الإرسال الملتبسة قبل إعادة تشغيل العامل على قاعدة مستعادة.
4. افصل بين إرجاع صورة التطبيق وإرجاع قاعدة SQLite. تحقق من توافق الصورة القديمة مع المخطط قبل تشغيلها، واحتفظ بنسخة لحالة ما قبل أي استعادة.

## نشر إصدار على Fly

1. سجّل SHA المطلوب في `main`، وتحقق من CI لنفس الإصدار. خذ نسخة قاعدة البيانات وأجرِ اختبار الاستعادة.
2. بعد الموافقة، شغّل `Deploy ChatPlatform to Fly.io` يدويًا من `main`. يتطلب نشر GitHub Actions توفّر `FLY_API_TOKEN`. ينفذ السير البناء ويستخدم التطبيق `chatplatform` ووحدة البيانات الحالية في `/data`.
3. من دليل المشروع نفّذ `python3 scripts/check_deployment.py --expected-revision <SHA>` بعد استبدال `<SHA>` بقيمة الالتزام. المطلوب تطابق `/health` مع SHA وعودة `/ready` بالحالة `200`.
4. اختبر تسجيل الدخول لكل دور مناسب، وقوائم الحسابات ضمن مؤسستها، وتحقق GET الخاص بـMeta، وصول POST موقّع، ظهور رسالة اختبار في Inbox، الرد، ثم إيصالًا حقيقيًا. اربط السجل بالنسخة ووقت الاختبار.
5. راجع سجلات Fly عند ظهور أخطاء `500` أو فشل العاملين. اربطها بتوقيت الطلب أو بمعرف التتبع وأبقِ قيم الأسرار خارج السجلات والتذاكر.

الدمج العادي في `main` يشغّل CI. سير النشر يدعم أيضًا حالة موثّقة تخص دمج تعديل لسير Fly نفسه مع علامة الموافقة على النشر. نشر صور GHCR من المسارات الحالية يحتاج تشغيلًا يدويًا من `main`. [ملفات سير العمل](../.github/workflows).

## تشخيص سريع

| المشاهدة | الإجراء |
|---|---|
| `/health` يعيد إصدارًا غير المطلوب أو بدون إصدار و`/ready` مفقود | راجع سجلات وسير Fly وتحقق من رقم النسخة المنشورة. ملفات الواجهة المحدثة تأتي من الصورة الجديدة. |
| `/ready` يعيد `503` | راجع اتصال SQLite ومسار الملف وتركيب وحدة البيانات. |
| فشل سير Fly برسالة `no access token available` | راجع توفر `FLY_API_TOKEN` لسير العمل في أسرار GitHub Actions. |
| `GET /webhook` يعيد `401` أو `503` | تحقق من توافق Verify Token أو من تهيئته، حسب الحالة. |
| `POST /webhook` يعيد `401` | تحقق من تهيئة App Secret ومرور الطلب الخام ورأس التوقيع كما وصل من Meta. |
| زر التحقق يفشل | راجع الرمز وWABA وPhone Number ID وحالة الحساب. تحقق من أن الرقم ضمن صفحة النتائج الأولى التي يفحصها الخادم. |
| الرسالة ظهرت `DeliveryUnknown` | صالح مع سجل Meta قبل قرار إرسال نسخة جديدة. |

آخر CI للأساس الموثق [اجتاز على main](https://github.com/moknethunter/ChatPlatform/actions/runs/45035053419). آخر محاولة نشر موثقة في هذا الدليل [توقفت قبل نشر الصورة](https://github.com/moknethunter/ChatPlatform/actions/runs/44952583664). يعتمد التحقق الحي على نتائج الإصدار المنشور وقت التشغيل.
