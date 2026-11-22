# مرجع API

هذه المسارات يعرّفها المشروع نفسه. المسارات نسبية إلى عنوان التطبيق. الواجهات الداخلية تستخدم JSON، وملفات الوسائط تستخدم multipart والتحميل الثنائي. ترجع المسارات القديمة غير المصرح بها `401` أو `403`.

## الجلسة والرؤوس

- سجّل الدخول بإرسال `username` و`password` إلى `POST /api/auth/login` واحفظ كوكي الجلسة عبر HTTPS. يعيد الخادم `username` و`tenantId` و`role`.
- أرسل `Content-Type: application/json` عند وجود جسم JSON. كل طلب تغيير تحت `/api`، بما فيه تسجيل الدخول والخروج، يتطلب `X-ChatPlatform-CSRF: 1`. إذا أدخل المتصفح الرأس `Origin` يجب أن يطابق أصل التطبيق. طلبات GET وHEAD وOPTIONS مستثناة من رأس التغيير.
- عند إرسال رسالة استخدم `Idempotency-Key` ثابتًا للمحاولة المنطقية نفسها. المفتاح اختياري وحده 128 محرفًا. أرسل رقم المرسل عند وجود أكثر من حساب نشط. سيختار الخادم موارد المؤسسة من هوية الجلسة.
- أمثلة الأجسام تعليمية. نفّذها مع قاعدة الاختبار والعميل المحاكى حسب [دليل الأوامر](../HELP.md).

| الحالة | الدلالة |
|---|---|
| `200` أو `201` | عادت البيانات أو أُنشئ المورد وفق العملية. |
| `202` | قُبلت الرسالة للطابور. عد إلى سجل الرسائل لتأكيد الإرسال والإيصال. |
| `204` | تمت العملية دون جسم استجابة. |
| `400` | صيغة أو قيمة غير صالحة. |
| `401` أو `403` | هوية غير صالحة أو سياسة وصول أو فشل التحقق من الطلب. |
| `404` | المورد غير متاح ضمن نطاق الحساب أو المسار غير موجود. |
| `409` | قيد تشغيلي أو تعارض نسخة أو حالة مورد. افحص كود `error` إن أعاده المسار. |
| `429` | تجاوز حد الطلبات أو العمليات المتزامنة. |
| `502` أو `503` | تعذر اتصال خارجي أو خدمة/تخزين غير جاهز بحسب المسار. |

## المسارات العامة والجلسة

| الطريقة والمسار | النطاق | الوظيفة |
|---|---|---|
| `GET /health` | عام | يعيد الحالة والوقت و`revision`. |
| `GET /ready` | عام | يختبر اتصال SQLite ويعيد `200` عند الجاهزية أو `503` عند فشل الاتصال. |
| `GET /webhook` | تحقق Meta | يتطلب `hub.mode=subscribe` و`hub.verify_token` صحيحًا و`hub.challenge`، ويعيد التحدي كنص. |
| `POST /webhook` | توقيع Meta | يتحقق من `X-Hub-Signature-256` على الجسم الخام ويخزن أحداث الرسائل والإيصالات. الحد 1 MiB. |
| `POST /api/auth/login` | عام مع رأس تغيير | إنشاء جلسة. يتطبق حد للمحاولات. |
| `GET /api/auth/me` | أي جلسة صالحة | هوية الجلسة ودورها والمؤسسة. |
| `POST /api/auth/logout` | أي جلسة صالحة | حذف سجل الجلسة وتسجيل الخروج. |

## المؤسسة والمحادثات

في الجداول التالية تعني **الفريق** أي جلسة `Admin` أو `Agent` في المؤسسة. **المدير** يعني `Admin` للمؤسسة نفسها.

| الطريقة والمسار | الدور | الوظيفة |
|---|---|---|
| `GET /api/platform-info` | الفريق | تعريف الخدمة والروابط الأساسية. |
| `GET /api/dashboard` | الفريق | مؤشرات المؤسسة وآخر المحادثات. |
| `GET /api/conversations` | الفريق | قائمة المحادثات بـ `search` و`status` و`assigned` و`automation` و`page` و`limit`. |
| `GET /api/conversations/{id}/messages` | الفريق | رسائل المحادثة الخاصة بالمؤسسة. |
| `PUT /api/conversations/{id}/automation` | الفريق | تحويل مسار المحادثة إلى `human` أو إعادته إلى `bot`. |
| `PUT /api/conversations/{id}/status` | الفريق | `open` أو `closed`. |
| `PUT /api/conversations/{id}/read` | الفريق | تصفير عداد غير المقروء. |
| `PUT /api/conversations/{id}/tags` | الفريق | تحديث الوسوم. |
| `PUT /api/conversations/{id}/assign` | المدير | تعيين موظف نشط من المؤسسة أو إلغاء التعيين. |
| `GET /api/live/inbox` | الفريق | بث SSE لإشارات المراجعة مع إعادة فحص الجلسة. |
| `GET /api/contacts`، `GET /api/contacts/{phone}` | الفريق | قائمة العملاء أو ملف عميل. |
| `POST /api/contacts`، `PUT /api/contacts/{phone}` | الفريق | إنشاء ملف أو تحديثه. |
| `GET /api/team`، `POST /api/team` | المدير | قائمة الموظفين أو إنشاء موظف. |
| `PUT /api/team/{username}/active`، `PUT /api/team/{username}/password` | المدير | تغيير حالة موظف أو كلمة مروره وإلغاء جلسته. |
| `GET /api/subscription`، `GET /api/reports?days=7` | المدير | استهلاك الاشتراك، أو تقرير آخر 1 إلى 30 يومًا بتوقيت UTC. |

## واتساب والقوالب والإرسال

| الطريقة والمسار | الدور | الوظيفة |
|---|---|---|
| `GET /api/whatsapp/accounts`، `GET /api/whatsapp/tenants` | الفريق | الحسابات المسجلة ضمن المؤسسة. |
| `GET /api/whatsapp/diagnostics`، `GET /api/whatsapp/webhook-status` | الفريق | مؤشرات الإعداد. القيم المجهولة لا تثبت حالة اتصال فعلية. |
| `POST /api/whatsapp/validate-credentials` | المدير | التحقق من رقم وWABA ورمز قبل التسجيل. |
| `POST /api/whatsapp/accounts` | المدير | تسجيل حساب بعد التحقق من الملكية والسعة. |
| `POST /api/whatsapp/accounts/{id}/verify` | المدير | فحص الحساب ورمزه المخزّن لدى Meta. |
| `PUT /api/whatsapp/accounts/{id}/token` | المدير | تحديث الرمز وإكمال WABA المفقود أو إعادة تفعيل حساب معطل بـ `reactivate`. |
| `DELETE /api/whatsapp/accounts/{id}` | المدير | إيقاف الحساب وإزالة الرموز مع حفظ التاريخ والملكية. |
| `GET /api/templates`، `GET /api/templates/{id}` | الفريق | قائمة القوالب أو قراءة قالب. مرّر `tenantId` المطابق للجلسة، و`whatsAppTenantId` الاختياري للتصفية عند عرض القائمة. |
| `POST /api/templates`، `DELETE /api/templates/{id}` | المدير | إنشاء قالب أو إيقافه ضمن نطاقه. يتطلب الإيقاف `tenantId` مطابقًا للجلسة في الاستعلام. |
| `POST /api/messages/send` | الفريق | حجز رسالة نصية في الطابور. |
| `POST /api/messages/send-template` | الفريق | حجز قالب باسم ولغة ومكونات اختيارية. |
| `POST /api/media/upload/{conversationId}` | الفريق | ملف multipart باسم `file`، حتى 5 MiB. يعيد تذكرة ترتبط بالمحادثة والمؤسسة لمدة 15 دقيقة. |
| `POST /api/messages/send-media` | الفريق | استخدام `conversationId` و`ticket` و`caption` الاختياري لحجز مرفق. |
| `GET /api/media/{messageId}` | الفريق | تنزيل أو عرض وسائط الرسالة المرتبطة بحساب المؤسسة. |

جسم رسالة الاختبار يقبل نصًا مباشرةً، أو نصًا داخل كائن `text.body`.

```json
{
  "to": "15550000001",
  "phoneNumberId": "999999999999",
  "text": { "body": "رسالة اختبار" }
}
```

## الأتمتة والمعرفة والتسويق

| الطريقة والمسار | الدور | الوظيفة |
|---|---|---|
| `GET /api/autoreply` | الفريق | قائمة قواعد الرد التقليدية. |
| `POST /api/autoreply`، `PUT /api/autoreply/{id}`، `PATCH /api/autoreply/{id}/toggle`، `DELETE /api/autoreply/{id}` | المدير | إنشاء القواعد وتحريرها وتبديل نشاطها وحذفها. |
| `GET /api/bots`، `GET /api/bots/{id}`، `POST /api/bots`، `PUT /api/bots/{id}` | المدير | قائمة مسودات البوت وقراءة مسودة وإنشاؤها وتحديثها. |
| `POST /api/bots/simulate` | المدير | محاكاة دون إرسال. |
| `POST /api/bots/{id}/publish`، `DELETE /api/bots/{id}/publish/{activationId}` | المدير | نشر نسخة محفوظة وإيقاف تنشيط محدد. |
| `GET /api/bots/active`، `GET /api/bots/runs` | المدير | حالة الإصدار وسجل التنفيذ. |
| `GET /api/knowledge`، `GET /api/knowledge/{id}`، `POST /api/knowledge`، `PUT /api/knowledge/{id}`، `DELETE /api/knowledge/{id}` | المدير | قائمة المقالات وتفاصيلها وتحريرها مع الإصدار. |
| `POST /api/knowledge/search`، `GET /api/knowledge/model`، `POST /api/knowledge/preview` | المدير | بحث محلي وحالة التهيئة ومعاينة اختيارية للنموذج الخاص. |
| `GET /api/marketing/consents`، `PUT /api/marketing/consent` | المدير | عرض الموافقات لكل حساب وتسجيلها أو سحبها. |
| `GET /api/marketing/lists`، `POST /api/marketing/lists`، `PUT /api/marketing/lists/{id}` | المدير | إدارة قوائم العملاء. |
| `GET /api/marketing/options` | المدير | حالة إتاحة الإرسال والقوالب المؤهلة مبدئيًا. |
| `GET /api/marketing/campaigns`، `POST /api/marketing/campaigns`، `POST /api/marketing/campaigns/{id}/cancel` | المدير | عرض الحملات وجدولتها وإلغاء المعلق. |

## مدير المنصة

كل هذه المسارات تتطلب `SuperAdmin` في النطاق المحجوز `__platform_owner__`.

| الطريقة والمسار | الوظيفة |
|---|---|
| `GET /api/platform-admin/plans` | قائمة الباقات. |
| `POST /api/platform-admin/plans` | إنشاء باقة وحدودها وسعر العرض. |
| `GET /api/platform-admin/tenants?page=1` | قائمة المؤسسات وحالة الاشتراك ومديريها. |
| `POST /api/platform-admin/tenants` | إنشاء مؤسسة ومديرها الأول واشتراكها. |
| `PUT /api/platform-admin/tenants/{id}/subscription` | تعيين الباقة وتاريخ الانتهاء وحالة الاشتراك ومراجعة الإصدار. |
| `PUT /api/platform-admin/tenants/{id}/active` | تغيير حالة المؤسسة. |
| `PUT /api/platform-admin/tenants/{id}/admins/{username}/password` | إعادة ضبط كلمة مرور المدير المدار وإلغاء جلساته. |
| `GET /api/platform-admin/audit` | آخر 100 عملية إدارية. |

## حقول الطلبات الشائعة

| الإجراء | حقول JSON أو الاستعلام |
|---|---|
| التحقق من بيانات Meta | `phoneNumberId` و`wabaId` و`accessToken`. يتطلب تسجيل الحساب أيضًا `tenantId` مطابقًا للجلسة. |
| تحديث رمز حساب | `accessToken` و`wabaId` عند الحاجة و`reactivate` لإعادة تفعيله صراحةً. |
| إنشاء قالب | `tenantId` و`whatsAppTenantId` و`name` و`category` و`language` و`body`. قالب الإرسال يستخدم `template.name` و`template.language.code` و`template.components` الاختيارية. |
| إضافة موظف | `username` و`displayName` و`password`. تغيير النشاط يستخدم `isActive`، وإعادة الضبط تستخدم `password`. |
| ملف عميل | `phone` و`displayName` و`email` و`notes` و`tags` و`stage`. التصفية حسب `search` و`stage`. |
| قاعدة الرد | `name` و`matchType` و`keywordPattern` و`replyText` و`priority` و`cooldownSeconds` و`isActive`. |
| مسودة البوت | `name` و`flow` و`version`. النشر يستخدم `version`، والمحاكاة تستخدم `flow` و`input` أو `inputs`. |
| مقال معرفة | `title` و`body` و`isPublished` و`version`. البحث والمعاينة يستخدمان `question`. |
| الموافقات والقوائم | الموافقة تستخدم `phoneNumberId` و`phone` و`granted` و`evidence`. القائمة تستخدم `name` و`phones` و`version`. |
| جدولة حملة | `id` و`name` و`listId` و`templateId` و`scheduledAt` بوقت ISO 8601 يحتوي على إزاحة أو UTC. |
| تسجيل باقة | `name` و`monthlyMessages` و`agents` و`accounts` و`bots` و`articles` و`campaigns` و`ownModel` و`monthlyPriceMinor` و`currency`. |
| تسجيل مؤسسة | `name` و`username` و`password` و`planId` و`endsAt`. تحديث الاشتراك يستخدم `planId` و`enabled` و`endsAt` و`version`. |

عند تعديل مورد يدعم `version` اقرأ الإصدار أحدث مرة ثم أعده في الطلب. يعيد الخادم `409` إذا تغير المورد منذ قراءته.

## المسارات المحجوبة

تخضع مسارات `/api/connectors` و`/api/events` و`/api/inbound/{platform}` و`/hub/chat` والمسارات القديمة `/api/whatsapp/messages` و`/api/whatsapp/send` و`/api/whatsapp/tenant-options` و`/api/whatsapp/exchange-code` و`/api/whatsapp/register-phone` للرفض الافتراضي. ينطبق ذلك أيضًا على `POST /api/whatsapp/tenants`. مسارات `/dev/seed` تُرجع `404` خارج بيئة التطوير، وهي خاضعة لسياسة الرفض الافتراضية داخلها في هذا الإصدار.

راجع [وحدات التحكم](../Controlers) لأنواع الطلبات الكاملة، و[سياسة الهوية](../Security/PlatformSecurity.cs) و[الـWebhook](../Infrastructure/Adapters/WhatsApp/WhatsAppExtensions.cs) لقواعد القبول.
