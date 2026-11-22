# مرجع الكود وشجرة المشروع

يغطي هذا المرجع ملفات أساس الإصدار `2b1543b`. يوضح مسؤولية كل ملف ويتضمن أسماء أنواع C# المعرّفة فيه، بما فيها السجلات والواجهات وأنواع الاختبارات. قد يتكرر اسم سجل طلب في ملفات مختلفة؛ رابط الملف يحدد سياقه.

## مسارات قراءة الكود

| المجال | نقطة الدخول | تابع إلى |
|---|---|---|
| تهيئة التطبيق | [Program.cs](../Program.cs) | [المهيئ](../Infrastructure/Data/DbInitializer.cs) و[ربط المسارات](../Endpoints/EndpointMapper.cs). |
| هوية وأدوار المدير | [PlatformSecurity.cs](../Security/PlatformSecurity.cs) | [لوحة مدير المنصة](../wwwroot/platform-admin.html) و[معالجها](../Controlers/PlatformAdminController.cs). |
| تسجيل رقم Meta | [WhatsAppController.cs](../Controlers/WhatsAppController.cs) | [ملكية الرقم](../Infrastructure/Adapters/WhatsApp/Binding/WhatsAppTenantResolver.cs) و[تشفير الرمز](../Infrastructure/Adapters/WhatsApp/WhatsAppTokenProtection.cs). |
| رسالة واردة | [WhatsAppExtensions.cs](../Infrastructure/Adapters/WhatsApp/WhatsAppExtensions.cs) | [عامل Inbox](../Infrastructure/Adapters/WhatsApp/WhatsAppInboxWorker.cs) و[المحادثات](../Controlers/ConversationsController.cs). |
| إرسال وإيصال | [MessagesController.cs](../Controlers/MessagesController.cs) | [عامل Outbox](../Infrastructure/Adapters/WhatsApp/WhatsAppOutboxWorker.cs) و[عامل الإيصالات](../Infrastructure/Adapters/WhatsApp/WhatsAppReceiptWorker.cs). |
| البوت والاشتراك | [BotDraftsController.cs](../Controlers/BotDraftsController.cs) | [تشغيل التدفقات](../Infrastructure/Bots/BotRuntime.cs) و[سياسة الحصص](../Infrastructure/Subscriptions/Entitlements.cs). |

## المجلدات

| المجلد | دوره |
|---|---|
| [`.github/`](../.github) | تعريف أتمتة المستودع. |
| [`.github/workflows/`](../.github/workflows) | CI، إصدار الصور، نشر Fly، وتدقيق النسخة. |
| [`Controlers/`](../Controlers) | معالجات HTTP والصلاحيات وأجسام الطلبات بحسب الوحدة. |
| [`Core/`](../Core) | عقود المحرك وأحداث المنصة الداخلية. |
| [`Core/Abstractions/`](../Core/Abstractions) | واجهات المحوّلات وناقل الأحداث. |
| [`Core/Events/`](../Core/Events) | شكل الحدث الذي ينتقل عبر الناقل. |
| [`Endpoints/`](../Endpoints) | ثوابت المسارات وتسجيل نقاط HTTP المشتركة. |
| [`Hubs/`](../Hubs) | تعريف SignalR القديم. |
| [`Infrastructure/`](../Infrastructure) | التخزين والتكاملات والمعالجة الخلفية. |
| [`Infrastructure/AI/`](../Infrastructure/AI) | البحث في المعرفة وعميل النموذج الخاص. |
| [`Infrastructure/Adapters/`](../Infrastructure/Adapters) | محوّلات الأحداث لمختلف التكاملات. |
| [`Infrastructure/Adapters/WhatsApp/`](../Infrastructure/Adapters/WhatsApp) | عميل Meta والـWebhook والوسائط والأعمال الخلفية. |
| [`Infrastructure/Adapters/WhatsApp/Binding/`](../Infrastructure/Adapters/WhatsApp/Binding) | ربط معرف الرقم بحساب المؤسسة النشط. |
| [`Infrastructure/Bots/`](../Infrastructure/Bots) | تعريف البوت وتقييمه وحالة التحويل للموظف. |
| [`Infrastructure/Configuration/`](../Infrastructure/Configuration) | هياكل إعدادات المحرك والموصلات. |
| [`Infrastructure/Data/`](../Infrastructure/Data) | إنشاء SQLite ونموذج EF Core. |
| [`Infrastructure/Data/Configurations/`](../Infrastructure/Data/Configurations) | خرائط خاصة لكيانات EF Core. |
| [`Infrastructure/Data/Entities/`](../Infrastructure/Data/Entities) | نماذج الجداول للمنصة ومجالاتها. |
| [`Infrastructure/Data/Migrations/`](../Infrastructure/Data/Migrations) | SQL المرجعي لمخطط الرسائل الدائمة. |
| [`Infrastructure/Engine/`](../Infrastructure/Engine) | الخدمة الخلفية الرئيسية لتوزيع الأحداث. |
| [`Infrastructure/Marketing/`](../Infrastructure/Marketing) | موافقة العملاء والتحقق من اعتماد القوالب. |
| [`Infrastructure/Messaging/`](../Infrastructure/Messaging) | تنفيذ ناقل الأحداث داخل العملية. |
| [`Infrastructure/Subscriptions/`](../Infrastructure/Subscriptions) | صلاحيات الباقات والاستهلاك. |
| [`Middleware/`](../Middleware) | معالجة أخطاء HTTP ومكوّن تكامل قديم. |
| [`Security/`](../Security) | الجلسة وملكية المؤسسة والأدوار والحماية من طلبات الغير. |
| [`scripts/`](../scripts) | أوامر التحقق والنشر وفحص الحاوية واستعادة البيانات. |
| [`tests/`](../tests) | اختبارات الواجهة والأمان والتخزين. |
| [`tests/MessagingSmoke/`](../tests/MessagingSmoke) | اختبار C# مع عميل Graph وبيانات مصطنعة. |
| [`wwwroot/`](../wwwroot) | الواجهات العربية وعميل JavaScript والتنسيق العام. |
| [`docs/`](./) | أدلة الاستخدام والتشغيل ومرجع المصدر والفروع. |

## الملفات والأنواع

### جذر المستودع

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`.dockerignore`](../.dockerignore) | يستبعد بيانات التشغيل والأسرار ومخرجات البناء من سياق Docker. | — |
| [`AGENTS.md`](../AGENTS.md) | قواعد المساهمة والعزل والاختبار وحماية أسرار المشروع. | — |
| [`ChatPlatform.csproj`](../ChatPlatform.csproj) | إصدار .NET ومرجعيات EF Core وData Protection وPolly وSQLite. | — |
| [`Dockerfile`](../Dockerfile) | يبني تطبيق .NET ثم ينسخه إلى صورة التشغيل ويربطها بفحص الجاهزية. | — |
| [`HELP.md`](../HELP.md) | مرجع أوامر التطوير والتشخيص والتشغيل والاستعادة. | — |
| [`Program.cs`](../Program.cs) | يهيئ التكوين وحقن الخدمات وقاعدة البيانات ثم يربط أمان التطبيق وHTTP. | `Program` |
| [`README.md`](../README.md) | نقطة دخول التوثيق وتعريف المنصة. | — |
| [`ROADMAP.md`](../ROADMAP.md) | سجل للمراحل والمهام المفتوحة؛ توضّح الأدلة حالة الأساس الموثق. | — |
| [`appsettings.json`](../appsettings.json) | القيم الافتراضية غير السرية للاستماع والتخزين وGraph وCORS وسجلات التطبيق. | — |
| [`docker-compose.yml`](../docker-compose.yml) | تعريف تشغيل نسخة الصورة بمتغيرات البيئة ووحدة بيانات دائمة. | — |
| [`fly.toml`](../fly.toml) | تعريف تطبيق Fly وبيئة HTTP وموقع الوحدة المركبة وفحص الصحة. | — |
| [`render.yaml`](../render.yaml) | تعريف خدمة Docker بديل لدى Render. | — |

### .github/workflows

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`build-and-push.yml`](../.github/workflows/build-and-push.yml) | سير يدوي لبناء الصورة ورفعها إلى GHCR من main. | — |
| [`ci.yml`](../.github/workflows/ci.yml) | يشغّل التحقق المعزول عند دفع تغيير إلى main. | — |
| [`deploy-fly.yml`](../.github/workflows/deploy-fly.yml) | يفحص الإصدار ثم ينشره على تطبيق Fly ويقارن الإصدار المنشور بالإصدار المطلوب. | — |
| [`dockerfile.yml`](../.github/workflows/dockerfile.yml) | سير يدوي بديل لبناء صورة Docker ورفعها إلى GHCR. | — |
| [`isolated-verification.yml`](../.github/workflows/isolated-verification.yml) | يبني النسخة ويشغّل فحوص HTTP وGraph محاكى وDocker ونسخ SQLite. | — |
| [`live-revision-audit.yml`](../.github/workflows/live-revision-audit.yml) | يرصد الحالات العامة لـFly من فرع التدقيق المخصص. | — |

### Controlers

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`AutoReplyController.cs`](../Controlers/AutoReplyController.cs) | يعرض قواعد الرد التقليدي ويدير محتواها وأولويتها وتفعيلها. | `AutoReplyController`، `RuleRequest` |
| [`BotDraftsController.cs`](../Controlers/BotDraftsController.cs) | يدير حفظ المسودات والمحاكاة والنشر والإيقاف وسجل التشغيل. | `BotDraftsController`، `SaveRequest`، `SimulateRequest`، `PublishRequest` |
| [`ContactsController.cs`](../Controlers/ContactsController.cs) | يعزل ملفات العملاء حسب المؤسسة ويدير بحثها وإنشاءها وتعديلها. | `ContactsController`، `ContactRequest` |
| [`ConversationsController.cs`](../Controlers/ConversationsController.cs) | يعرض المحادثات والرسائل ويحدّث الحالة والوسوم والإسناد والتحويل للبشر. | `ConversationsController`، `AutomationRequest`، `StatusRequest`، `AssignRequest`، `TagsRequest` |
| [`DashboardController.cs`](../Controlers/DashboardController.cs) | يجمع مؤشرات مساحة العمل وآخر المحادثات للمؤسسة الحالية. | `DashboardController` |
| [`DevSeedController.cs`](../Controlers/DevSeedController.cs) | يضم مسارات البذر القديمة والخاصة بالتطوير؛ راجع حالة الحجب في مرجع API. | `DevSeedController`، `SeedReq` |
| [`InboxLiveController.cs`](../Controlers/InboxLiveController.cs) | يبث إشارات مراجعة Inbox عبر SSE ويفحص الجلسة أثناء البث. | `InboxLiveController` |
| [`KnowledgeController.cs`](../Controlers/KnowledgeController.cs) | يدير المقالات والبحث المحلي ومعاينة نموذج المالك. | `KnowledgeController`، `ArticleRequest`، `QuestionRequest` |
| [`MarketingController.cs`](../Controlers/MarketingController.cs) | يدير الموافقات والقوائم والحملات والإلغاء وحدود الجدولة. | `MarketingController`، `ConsentRequest`، `ListRequest`، `ScheduleRequest` |
| [`MediaController.cs`](../Controlers/MediaController.cs) | يرفع ملفات المحادثة إلى Meta ويصدر التذاكر ويتحقق من ملكية الوسائط عند التنزيل. | `MediaController`، `Ticket` |
| [`MessagesController.cs`](../Controlers/MessagesController.cs) | يتحقق من النص والقالب والمرفق ثم يحجزه في Outbox ضمن نطاق المؤسسة. | `MessagesController`، `SendTextRequest`، `SendTemplateRequest`، `SendMediaRequest` |
| [`PlatformAdminController.cs`](../Controlers/PlatformAdminController.cs) | يدير الباقات والمؤسسات والاشتراكات وحساباتها الإدارية وسجل العمليات. | `PlatformAdminController`، `PlanRequest`، `NewTenantRequest`، `SubscriptionRequest`، `ActiveRequest`، `PasswordRequest` |
| [`ReportsController.cs`](../Controlers/ReportsController.cs) | يجمع إحصاءات الرسائل وحالات الطابور والأتمتة والموافقات. | `ReportsController` |
| [`SubscriptionController.cs`](../Controlers/SubscriptionController.cs) | يعرض الباقة والحدود والاستهلاك للمؤسسة. | `SubscriptionController` |
| [`TeamController.cs`](../Controlers/TeamController.cs) | ينشئ حسابات الموظفين ويدير نشاطهم وكلمات مرورهم وإلغاء جلساتهم. | `TeamController`، `CreateAgentRequest`، `ResetPasswordRequest`، `SetActiveRequest` |
| [`TenantsController.cs`](../Controlers/TenantsController.cs) | يعرض قائمة حسابات واتساب في المؤسسة عبر مسار التوافق ويحتفظ بمعالج كتابة قديم محجوب. | `TenantsController`، `CreateTenantRequest` |
| [`WebhookControler.cs`](../Controlers/WebhookControler.cs) | يحفظ مثال Webhook قديمًا داخل تعليق؛ المسار العامل في WhatsAppExtensions. | `WebhookController` |
| [`WhatsAppController.cs`](../Controlers/WhatsAppController.cs) | يربط الحساب بحساب أعمال Meta ويدير الرموز والتحقق وإعادة التفعيل والتشخيص. | `WhatsAppController`، `ValidateMetaRequest`، `CreateTenantRequest`، `UpdateTokenRequest`، `SendMessageDto` |
| [`WhatsAppOnboardingController.cs`](../Controlers/WhatsAppOnboardingController.cs) | يحتفظ بكود مسارات تبادل رمز OAuth وتسجيل الهاتف ضمن المسارات المحجوبة. | `WhatsAppOnboardingController`، `ExchangeReq`، `RegisterReq` |
| [`WhatsAppTemplatesController.cs`](../Controlers/WhatsAppTemplatesController.cs) | يدير قوالب واتساب حسب المؤسسة والحساب ويربط الإنشاء بنظام قوالب Meta. | `WhatsAppTemplatesController`، `CreateTemplateRequest` |

### Core/Abstractions

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`IEventsBus.cs`](../Core/Abstractions/IEventsBus.cs) | عقد نشر أحداث المنصة وقراءتها. | `IEventBus` |
| [`IIntegrationAdapter.cs`](../Core/Abstractions/IIntegrationAdapter.cs) | عقد موحد لمحوّلات التكامل وتلقيها أحداث المحرك. | `IIntegrationAdapter` |

### Core/Events

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`PlatformEvent.cs`](../Core/Events/PlatformEvent.cs) | ظرف الحدث ونوعه ومصدره وحمولته. | `PlatformEvent` |

### Endpoints

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`Apiendpoints.cs`](../Endpoints/Apiendpoints.cs) | ثوابت مسارات API والـWebhook والـHub. | `ApiEndpoints` |
| [`EndpointMapper.cs`](../Endpoints/EndpointMapper.cs) | يربط الصحة والجاهزية ومعلومات الخدمة ومسارات HTTP بخط التطبيق. | `EndpointMapper` |

### Hubs

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`ChatHub.cs`](../Hubs/ChatHub.cs) | واجهة SignalR القديمة؛ الوصول إليها يخضع للرفض الافتراضي. | `ChatHub` |

### Infrastructure/AI

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`KnowledgeSearch.cs`](../Infrastructure/AI/KnowledgeSearch.cs) | يرتّب نتائج البحث النصي في المقالات المنشورة للمؤسسة. | `KnowledgeSearch`، `Match` |
| [`OwnModelClient.cs`](../Infrastructure/AI/OwnModelClient.cs) | يستدعي مضيف نموذج المالك للمعاينة باستخدام عقد ورسالة محدودين. | `OwnModelClient`، `ModelRequest`، `ModelResponse` |

### Infrastructure/Adapters

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`ChatAdapter.cs`](../Infrastructure/Adapters/ChatAdapter.cs) | محوّل أحداث المحادثات في الناقل الداخلي. | `ChatAdapter` |
| [`ConsoleAdapter.cs`](../Infrastructure/Adapters/ConsoleAdapter.cs) | محوّل يعرض أحداث التشغيل في إخراج المنصة. | `ConsoleAdapter` |
| [`UniversalConnectorAdapter.cs`](../Infrastructure/Adapters/UniversalConnectorAdapter.cs) | ينفذ وصلات مرتبطة بتهيئة الموصل في خط الأحداث الداخلي. | `UniversalConnectorAdapter` |
| [`WebhookAdapter.cs`](../Infrastructure/Adapters/WebhookAdapter.cs) | محوّل يتعامل مع أحداث Webhook العامة في البنية الداخلية. | `WebhookAdapter` |

### Infrastructure/Adapters/WhatsApp/Binding

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`WhatsAppTenantResolver.cs`](../Infrastructure/Adapters/WhatsApp/Binding/WhatsAppTenantResolver.cs) | يطابق Phone Number ID مع حسابه ومؤسسته النشطين. | `IWhatsAppTenantResolver`، `WhatsAppTenantResolver` |

### Infrastructure/Adapters/WhatsApp

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`IWhatsAppCloudClient.cs`](../Infrastructure/Adapters/WhatsApp/IWhatsAppCloudClient.cs) | واجهة عميل Meta وشكل نتيجة إرسال الرسالة. | `IWhatsAppCloudClient`، `WhatsAppSendResult` |
| [`WhatsAppAdapter.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppAdapter.cs) | يعالج أحداث التكامل مع واتساب ويستخدم العميل المناسب. | `WhatsAppAdapter` |
| [`WhatsAppAutoReplyAdapter.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppAutoReplyAdapter.cs) | معالج الردود القديم؛ يتجاوز ما سجّل العامل الدائم معالجته. | `WhatsAppAutoReplyAdapter` |
| [`WhatsAppCloudClient.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppCloudClient.cs) | عميل طلبات إرسال WhatsApp Cloud API ومعالجة نتائجها. | `WhatsAppCloudClient` |
| [`WhatsAppEventKeys.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppEventKeys.cs) | يبني مفاتيح ربط أحداث واتساب ومنع التكرار. | `WhatsAppEventKeys` |
| [`WhatsAppExtensions.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppExtensions.cs) | يسجّل عملاء وعمال واتساب ويعرّف GET وPOST للـWebhook وتخزين الحدث الموقّع. | `WhatsAppExtensions`، `JsonElementExtensions` |
| [`WhatsAppIdempotencyStore.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppIdempotencyStore.cs) | مخزن منع التكرار لمسار معالجة أحداث التكامل الداخلي. | `WhatsAppIdempotencyStore` |
| [`WhatsAppInboxWorker.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppInboxWorker.cs) | يعالج أحداث الرسائل الدائمة ويحدّث العميل والمحادثة ويشغّل الأتمتة. | `WhatsAppInboxWorker` |
| [`WhatsAppMediaService.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppMediaService.cs) | يتعرف على صيغ الوسائط ويدير نقلها إلى Meta وتحميلها بحدود واضحة. | `WhatsAppMediaService`، `MediaKind`، `Downloaded` |
| [`WhatsAppOnboardingService.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppOnboardingService.cs) | خدمة تبادل الرموز وقراءة WABA وحفظ الربط ضمن مسار onboarding القديم. | `WhatsAppOnboardingService`، `ExchangeTokenResult`، `TenantSaveResult`، `WabaDetailsResult` |
| [`WhatsAppOptions.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppOptions.cs) | خيارات إعداد واتساب وGraph والتحقق من الطلبات. | `WhatsAppOptions` |
| [`WhatsAppOutboxWorker.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppOutboxWorker.cs) | ينفّذ الإرسال الدائم ويحسم المطالبات والمحاولات وحالة النتيجة الملتبسة. | `WhatsAppOutboxWorker` |
| [`WhatsAppReceiptWorker.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppReceiptWorker.cs) | يطابق إيصالات Meta مع الرسائل ويحدّث التسليم والقراءة. | `WhatsAppReceiptWorker` |
| [`WhatsAppSignatureValidator.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppSignatureValidator.cs) | يتحقق من قيمة HMAC على الجسم الخام. | `WhatsAppSignatureValidator` |
| [`WhatsAppTokenProtection.cs`](../Infrastructure/Adapters/WhatsApp/WhatsAppTokenProtection.cs) | يشفّر الرموز ويقرأ القيم المحمية بصيغة قديمة أو حالية. | `WhatsAppTokenProtection` |

### Infrastructure/Bots

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`BotFlow.cs`](../Infrastructure/Bots/BotFlow.cs) | يعرّف بنية التدفق ويتحقق من الرسم ويقيّم تنفيذ خطوة. | `BotFlow`، `Node`، `Definition`، `Evaluation` |
| [`BotRuntime.cs`](../Infrastructure/Bots/BotRuntime.cs) | يحمّل النسخة المنشورة ويطبّق المؤشر وقواعد معالجة الرسالة الآلية. | `BotRuntime` |
| [`HumanHandoff.cs`](../Infrastructure/Bots/HumanHandoff.cs) | يغيّر وضع المحادثة ويلغي الردود الآلية المعلقة المرتبطة بها. | `HumanHandoff` |

### Infrastructure/Configuration

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`ConnectorConfig.cs`](../Infrastructure/Configuration/ConnectorConfig.cs) | نموذج إعداد الموصل الذي يقرأه محوّل التكامل. | `ConnectorConfig` |
| [`DynamicSettings.cs`](../Infrastructure/Configuration/DynamicSettings.cs) | بيانات الإعدادات الديناميكية المستخدمة لدى الموصلات. | `DynamicSettings` |
| [`EngineOptions.cs`](../Infrastructure/Configuration/EngineOptions.cs) | خيارات التهيئة العامة للمحرك الداخلي. | `EngineOptions` |

### Infrastructure/Data/Configurations

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`ConnectorConfiguration.cs`](../Infrastructure/Data/Configurations/ConnectorConfiguration.cs) | يضبط خريطة EF Core لكيان الموصل. | `ConnectorConfiguration` |

### Infrastructure/Data

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`DbInitializer.cs`](../Infrastructure/Data/DbInitializer.cs) | ينشئ مخطط SQLite ويرحّل حقول الإصدارات القديمة ويهيئ الربط الافتراضي. | `DbInitializer` |
| [`PlatformDbContext.cs`](../Infrastructure/Data/PlatformDbContext.cs) | يسجّل DbSets وعلاقات EF Core والفهارس ومخزن مفاتيح حماية البيانات. | `PlatformDbContext` |

### Infrastructure/Data/Entities

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`AutoReplyRuleEntity2.cs`](../Infrastructure/Data/Entities/AutoReplyRuleEntity2.cs) | كيان قاعدة الرد التقليدي مع ترتيب ونمط وفترة تهدئة. | `AutoReplyRuleEntity` |
| [`AutomationRunEntity.cs`](../Infrastructure/Data/Entities/AutomationRunEntity.cs) | سجل تنفيذ الأتمتة المرتبط برسالة واردة ونتيجة. | `AutomationRunEntity` |
| [`BotDraftEntity.cs`](../Infrastructure/Data/Entities/BotDraftEntity.cs) | بيانات المسودة القابلة للتعديل ومراجعتها. | `BotDraftEntity` |
| [`BotReleaseEntity.cs`](../Infrastructure/Data/Entities/BotReleaseEntity.cs) | لقطة البوت المنشورة ومعرّف تفعيلها. | `BotReleaseEntity` |
| [`ConnectorEntity.cs`](../Infrastructure/Data/Entities/ConnectorEntity.cs) | سجل تكوين موصّل داخلي. | `ConnectorEntity` |
| [`ConversationAutomationEntity.cs`](../Infrastructure/Data/Entities/ConversationAutomationEntity.cs) | وضع البوت أو الموظف ومؤشر انتظار المحادثة. | `ConversationAutomationEntity` |
| [`CrmContactEntity.cs`](../Infrastructure/Data/Entities/CrmContactEntity.cs) | ملف العميل داخل المؤسسة والمرحلة والوسوم. | `CrmContactEntity` |
| [`InboxRevisionEntity.cs`](../Infrastructure/Data/Entities/InboxRevisionEntity.cs) | مراجعة Inbox المعزولة التي تدفع تجديد الواجهة. | `InboxRevisionEntity` |
| [`KnowledgeArticleEntity.cs`](../Infrastructure/Data/Entities/KnowledgeArticleEntity.cs) | مقال معرفة للمؤسسة بحالة نشر ورقم نسخة. | `KnowledgeArticleEntity` |
| [`ManagedAgentEntity.cs`](../Infrastructure/Data/Entities/ManagedAgentEntity.cs) | هوية موظف ومدخل تجزئة كلمة المرور وحالة التفعيل. | `ManagedAgentEntity` |
| [`MarketingEntities.cs`](../Infrastructure/Data/Entities/MarketingEntities.cs) | كيانات الموافقة وسجلها وقائمة العملاء والحملة والمستلم. | `MarketingConsentEntity`، `ConsentEventEntity`، `CustomerListEntity`، `MarketingCampaignEntity`، `CampaignRecipientEntity` |
| [`PlatformSessionEntity.cs`](../Infrastructure/Data/Entities/PlatformSessionEntity.cs) | معرف جلسة مستخدم ومالكها ووقت انتهائها. | `PlatformSessionEntity` |
| [`SubscriptionEntities.cs`](../Infrastructure/Data/Entities/SubscriptionEntities.cs) | كيانات مدير المؤسسة والباقة والاشتراك وسجل مدير المنصة. | `ManagedTenantAdminEntity`، `ServicePlanEntity`، `TenantSubscriptionEntity`، `PlatformAdminAuditEntity` |
| [`TenantEntity.cs`](../Infrastructure/Data/Entities/TenantEntity.cs) | هوية المؤسسة وحالة تفعيلها. | `TenantEntity` |
| [`WhatsAppConversationEntity.cs`](../Infrastructure/Data/Entities/WhatsAppConversationEntity.cs) | ربط قناة واتساب ورقم العميل والمحادثة ونافذة الخدمة. | `WhatsAppConversationEntity` |
| [`WhatsAppMessageEntity.cs`](../Infrastructure/Data/Entities/WhatsAppMessageEntity.cs) | سجل رسالة واردة أو صادرة وربطها بالمحادثة وWAMID. | `WhatsAppMessageEntity` |
| [`WhatsAppOutboxEntity.cs`](../Infrastructure/Data/Entities/WhatsAppOutboxEntity.cs) | طلب إرسال دائم ومحاولاته وأوقات المطالبة والإرسال. | `WhatsAppOutboxEntity` |
| [`WhatsAppStatusInboxEntity.cs`](../Infrastructure/Data/Entities/WhatsAppStatusInboxEntity.cs) | إيصال Meta المحفوظ حتى يطابقه عامل المعالجة. | `WhatsAppStatusInboxEntity` |
| [`WhatsAppTemplateEntity.cs`](../Infrastructure/Data/Entities/WhatsAppTemplateEntity.cs) | معلومات قالب واتساب ولغته ورقمه لدى Meta ونطاقه. | `WhatsAppTemplateEntity` |
| [`WhatsAppTenantEntity.cs`](../Infrastructure/Data/Entities/WhatsAppTenantEntity.cs) | حساب ورقم Meta داخل المؤسسة ومرجع WABA والرمز المشفر. | `WhatsAppTenantEntity` |
| [`WhatsAppWebhookInboxEntity.cs`](../Infrastructure/Data/Entities/WhatsAppWebhookInboxEntity.cs) | حدث ويب هوك وارد مخزن للمعالجة الدائمة. | `WhatsAppWebhookInboxEntity` |

### Infrastructure/Data/Migrations

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`20261102_AddDurableMessaging.sql`](../Infrastructure/Data/Migrations/20261102_AddDurableMessaging.sql) | مرجع تغييرات SQL الخاصة بالجداول الأولى للرسائل الدائمة. | — |

### Infrastructure/Engine

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`CoreEngine.cs`](../Infrastructure/Engine/CoreEngine.cs) | خدمة خلفية تربط ناقل الأحداث بمحوّلات التكامل. | `CoreEngine` |

### Infrastructure/Marketing

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`Consent.cs`](../Infrastructure/Marketing/Consent.cs) | قواعد الموافقة وإلغاء الاشتراك ومعالجة الكلمات الدالة. | `Consent` |
| [`TemplateApproval.cs`](../Infrastructure/Marketing/TemplateApproval.cs) | يقارن القالب المحلي بموافقة Meta قبل التسليم التسويقي. | `TemplateApproval` |

### Infrastructure/Messaging

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`InMemoryEventBus.cs`](../Infrastructure/Messaging/InMemoryEventBus.cs) | ناقل أحداث بقناة في الذاكرة لأحداث التكامل الداخلي. | `InMemoryEventBus` |

### Infrastructure/Subscriptions

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`Entitlements.cs`](../Infrastructure/Subscriptions/Entitlements.cs) | سياسة إتاحة الاشتراك وحصصه وحجز الرسائل وإلغاء المعلقة. | `Entitlements`، `Access` |

### Middleware

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`ApiKeyMiddleware.cs`](../Middleware/ApiKeyMiddleware.cs) | مكوّن مفتاح API القديم؛ المصادقة الفعلية للواجهات المصرح بها عبر جلسة. | `ApiKeyMiddleware` |
| [`GlobalExceptionMiddleware.cs`](../Middleware/GlobalExceptionMiddleware.cs) | يتعامل مع استثناءات HTTP ويعيد معرف تتبع للتشخيص. | `GlobalExceptionMiddleware` |

### Security

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`PlatformSecurity.cs`](../Security/PlatformSecurity.cs) | المصادقة والسياسات والجلسات وحدود الطلبات وفحص مصدر الكتابة. | `PlatformAccount`، `PlatformSecurity`، `LoginRequest` |

### scripts

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`check_deployment.py`](../scripts/check_deployment.py) | يقارن SHA المنشور ويتحقق من استجابة الصحة والجاهزية. | — |
| [`container_healthcheck.sh`](../scripts/container_healthcheck.sh) | يفحص /ready داخل حاوية التشغيل عبر اتصال محلي. | — |
| [`database_recovery.py`](../scripts/database_recovery.py) | أداة SQLite online backup واستعادة أوفلاين والتحقق من السلامة والبصمة. | — |

### tests/MessagingSmoke

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`MessagingSmoke.csproj`](../tests/MessagingSmoke/MessagingSmoke.csproj) | مشروع عميل Graph مصطنع وتشغيل اختبارات الرسائل المعزولة. | — |
| [`Program.cs`](../tests/MessagingSmoke/Program.cs) | يتحقق من الترحيل والصلاحيات والرموز والوسائط والإرسال عبر معالجات وهمية. | `FakeGraphHandler`، `FakeMediaFactory`، `FakeMediaHandler`، `FakeAccountFactory`، `FakeAccountHandler` |

### tests

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`backup_smoke.py`](../tests/backup_smoke.py) | يتحقق من اللقطة المتسقة واسترجاع النسخة وإلغاء الجلسات باستخدام قاعدة اختبار. | — |
| [`check_web_assets.py`](../tests/check_web_assets.py) | يفحص صياغة JavaScript والروابط الثابتة ومعرّفات عناصر HTML. | — |
| [`security_smoke.py`](../tests/security_smoke.py) | ينشئ بيانات وهويات مؤقتة ويفحص سياسات HTTP وHMAC والعزل والطوابير. | — |

### wwwroot

| الملف | المسؤولية | الكلاسات والسجلات والواجهات |
|---|---|---|
| [`api.js`](../wwwroot/api.js) | عميل HTTP والتنقل حسب الجلسة والرؤوس وترجمة الأخطاء. | — |
| [`autoreply.html`](../wwwroot/autoreply.html) | إعادة توجيه إلى صفحة قواعد الرد ذات الاسم التاريخي. | — |
| [`autoteplay.html`](../wwwroot/autoteplay.html) | واجهة إدارة قواعد الرد الآلي التقليدية. | — |
| [`bots.html`](../wwwroot/bots.html) | محرر تدفقات البوت والمحاكاة والنشر والتنشيط. | — |
| [`connectors.html`](../wwwroot/connectors.html) | واجهة موصلات قديمة ومسارات تكاملها محجوبة. | — |
| [`contacts.html`](../wwwroot/contacts.html) | بحث العملاء وعرض البيانات وتحريرها. | — |
| [`data-deletion.html`](../wwwroot/data-deletion.html) | صفحة سياسة وطلب حذف البيانات العامة. | — |
| [`inbox.html`](../wwwroot/inbox.html) | صندوق المحادثات والرد والوسائط والتحويل للموظف وتصفية المحادثات. | — |
| [`index.html`](../wwwroot/index.html) | لوحة مؤشرات المؤسسة ومداخل المساحات وآخر المحادثات. | — |
| [`knowledge.html`](../wwwroot/knowledge.html) | إدارة المعرفة والبحث والمعاينة لنموذج المالك. | — |
| [`login.html`](../wwwroot/login.html) | نموذج الدخول والتحويل إلى مساحة الدور الصحيح. | — |
| [`marketing.html`](../wwwroot/marketing.html) | نماذج الموافقات والقوائم وجدولة الحملات وإلغائها. | — |
| [`platform-admin.html`](../wwwroot/platform-admin.html) | مساحة مدير المنصة لإدارة المؤسسات والباقات والاشتراكات والسجل. | — |
| [`platform.css`](../wwwroot/platform.css) | لغة التصميم المشتركة وأسلوب عرض صفحات RTL. | — |
| [`privacy.html`](../wwwroot/privacy.html) | سياسة الخصوصية العامة. | — |
| [`reports.html`](../wwwroot/reports.html) | واجهة مؤشرات الرسائل والطوابير والنتائج ضمن مدة محددة. | — |
| [`settings.html`](../wwwroot/settings.html) | واجهة إعدادات المؤسسة والتشخيص والتنقل. | — |
| [`subscription.html`](../wwwroot/subscription.html) | واجهة الباقة والاشتراك والحصص والاستهلاك. | — |
| [`team.html`](../wwwroot/team.html) | إدارة موظفي المؤسسة وحالاتهم وكلمات مرورهم. | — |
| [`templates.html`](../wwwroot/templates.html) | واجهة قوالب حسابات واتساب وإدارتها. | — |
| [`terms.html`](../wwwroot/terms.html) | شروط الاستخدام العامة. | — |
| [`test-whatsapp.html`](../wwwroot/test-whatsapp.html) | واجهة فحص واتساب وأدوات الاختبار من الواجهة. | — |
| [`whatsapp.html`](../wwwroot/whatsapp.html) | ربط حسابات واتساب وإيقافها وفحص الرمز المحفوظ وإعداد Webhook. | — |

### الأدلة الجديدة

| الملف | استخدامه |
|---|---|
| [`architecture.md`](architecture.md) | مسارات البيانات والرسائل والأدوار. |
| [`user-guide.md`](user-guide.md) | تشغيل مساحات مدير المنصة والمؤسسة والموظف. |
| [`api.md`](api.md) | المسارات والأدوار والعقود التشغيلية. |
| [`operations.md`](operations.md) | إعداد وإطلاق وفحص النسخة وحماية البيانات. |
| [`branches.md`](branches.md) | المرجع المؤرخ لأفرع المستودع. |
| [`source-map.md`](source-map.md) | الملف الحالي لفهرسة الأدلة والكود. |
