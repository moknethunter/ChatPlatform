# Source comment coverage / تغطية التعليقات البرمجية

This is an incremental documentation pass, not a claim that every line in the repository has been annotated. The first pass adds bilingual, statement-level comments to the following real source files (252 new comment lines):
| File | New explanatory comments |
|---|---:|
| Core/Abstractions/IDatabaseAdapter.cs | 4 |
| Core/Abstractions/IIntegrationAdapter.cs | 4 |
| Infrastructure/Adapters/Databases/SqliteDatabaseAdapter.cs | 10 |
| Infrastructure/Adapters/Databases/PostgreSqlDatabaseAdapter.cs | 19 |
| Infrastructure/Data/Connections/ExternalDatabaseRegistry.cs | 56 |
| Infrastructure/Bots/BotFlow.cs | 53 |
| Infrastructure/Bots/BotRuntime.cs | 59 |
| Infrastructure/Adapters/WhatsApp/DatabaseSignupAttemptStore.cs | 26 |
| Controlers/DatabaseConnectionsController.cs | 21 |

These comments cover meaningful declarations, constraints, branches and resource lifetimes, rather than adding noise to braces or blank lines. Existing code and behavior are retained; CI must still verify compilation. Subsequent passes must cover Program.cs, Security, WhatsApp workers and remaining controllers, data entities/initialization, remaining services, web assets, scripts, tests and deployment configuration. For formats that do not support comments (JSON) and code embedded in strings, use companion documentation referring to exact statements instead of invalid syntax.

المرحلة الحالية ليست تغطية شاملة لكل سطر. أكمل الملفات المتبقية تدريجيًا، وراجع كل تغيير على حدة مع اختبارات البناء والأمان. لا تستبدل التعليقات الدقيقة بتعليقات آلية تكرر أسماء المتغيرات أو تكشف الأسرار.
