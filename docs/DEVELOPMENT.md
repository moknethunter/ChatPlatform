# ChatPlatform — developer and maintainer guide / دليل المطور
[Arabic help](HELP-AR.md) | [English help](HELP-EN.md) | [Scale plan](SCALABILITY.md)

## 1. Scope and invariants / المبادئ
Keep the existing ASP.NET Core modular application, current primary SQLite database and existing Meta test integration. Add external integrations via independent adapters; do not silently migrate or split internal storage. Target 1,000,000 registered, linkable and manageable institutions, not a claim of simultaneous traffic. No mandatory paid AI. No merge, production migration or deploy without approval.
حافظ على المعمارية الحالية وعزل المؤسسات، ولا تحول الاختبار الاصطناعي إلى ادعاء جاهزية إنتاج.

## 2. Source map / خريطة الكود
| Path | Responsibility and change rules |
|---|---|
| Program.cs | Composition root: configuration normalization, EF, data protection, DI, middleware, hosted services and endpoints. Keep secrets out of logs and preserve startup failure behavior. |
| Core/Abstractions/IIntegrationAdapter.cs | Event adapter contract. Handlers must filter event types and be idempotent. |
| Core/Abstractions/IDatabaseAdapter.cs | External read-only database connection/transaction contract. Does not replace PlatformDbContext. |
| Infrastructure/Engine/CoreEngine.cs | Bounded channel, event adapter dispatch, retry/jitter, metrics. Not the durable WhatsApp queue. |
| Infrastructure/Messaging/InMemoryEventBus.cs | Process-local subscriptions. Do not assume durable or distributed delivery. |
| Infrastructure/Adapters/WhatsApp/WhatsAppExtensions.cs | Signed webhook ingress, ownership resolution and durable inbox/receipt insertion; DI for Graph clients and workers. |
| Infrastructure/Adapters/WhatsApp/WhatsAppInboxWorker.cs | Due row claim, signed-message persistence, conversation update, STOP or BotRuntime, durable commit and notification. |
| Infrastructure/Adapters/WhatsApp/WhatsAppOutboxWorker.cs | Atomic claim, account/tenant/subscription/window/consent checks, Meta dispatch, outcome. Never blindly retry ambiguous delivery. |
| Infrastructure/Adapters/WhatsApp/WhatsAppReceiptWorker.cs | Tenant/account/contact/message matching; monotonic receipt states. |
| Infrastructure/Adapters/WhatsApp/DatabaseSignupAttemptStore.cs | Hashed correlation and session IDs, atomic conditional consumption; not proof of Meta ownership. |
| Infrastructure/Data/PlatformDbContext.cs | EF entity mappings and primary persistence. |
| Infrastructure/Data/DbInitializer.cs | Idempotent schema creation, compatibility migration, indexes and triggers. Preserve existing rows and data-protection keys. |
| Infrastructure/Data/Entities/ | Persistent domain records; tenant scope is part of ownership. |
| Infrastructure/Data/Connections/ExternalDatabaseRegistry.cs | Resolve configured aliases by tenant, bind approved parameters, bound I/O. Never expose SQL or connection strings. |
| Infrastructure/Adapters/Databases/ | Separate provider implementations. |
| Infrastructure/Bots/BotFlow.cs | Pure validated DAG evaluator shared by simulation/runtime. |
| Infrastructure/Bots/BotRuntime.cs | Inbound-triggered bot state and outbox staging; no direct remote send. |
| Infrastructure/Subscriptions/Entitlements.cs | Plan access and quota policy; check atomically with resource creation. |
| Security/PlatformSecurity.cs | Session authentication, role policies, CSRF, rate limits and tenant identity. |
| Controlers/ | HTTP API controllers (repository spelling intentional). Authorize each action; server derives tenant. |
| Controlers/PlatformAdminController.cs | SuperAdmin provisioning, cursor lookup, subscription/suspension, audit. |
| Controlers/BotDraftsController.cs | Versioned drafts, validation, side-effect-free simulation and immutable active release. |
| Controlers/DatabaseConnectionsController.cs | Admin-only alias catalog and sanitized probes. |
| wwwroot/api.js | Central browser request helpers; keep CSRF, cancellation, retries and idempotency consistent. |
| wwwroot/*.html, platform.css | RTL web pages and common design. Render untrusted text as textContent, not HTML. |
| tests/ | Isolated HTTP, mocked Graph, backup, UI syntax and synthetic directory tests. |
| scripts/ | Operator backup/restore and deployment verification. |
| .github/workflows/ | CI and explicitly authorized release workflows; inspect trigger before merging. |
| Dockerfile, docker-compose.yml, fly.toml | Container and host configuration. Preserve /data mount and credentials. |

## 3. Message and security lifecycle / دورة الرسالة
GET webhook verifies the configured challenge; POST verifies HMAC of bounded raw bytes before parsing, resolves account ownership and persists deduplicated events. The inbox worker claims due work, checks lease and idempotency, saves inbound message, automation receipt and outgoing reservation together. The outbox worker checks ownership and current policy before calling Graph; successful outbox and message evidence now commit together. Receipts are matched by tenant, phone number, recipient and Meta message ID. The in-memory bus is supplemental; no distributed exactly-once guarantee.
Use authenticated tenant claims or verified channel ownership, never request body tenant IDs. Scope queries/joins by tenant; deny unknown endpoints and cross-tenant operations. Browser writes require CSRF header. Distinguish Admin/Agent/SuperAdmin. Protect tokens, session IDs, customer content and external connection strings. Avoid leaking provider errors or raw payloads. Old UniversalConnectorAdapter/global connector settings are legacy; do not expose or reuse them for new tenant-owned actions before a security review.

## 4. Build and test / التشغيل والاختبار
Requirements: Git, .NET SDK 8, Python 3, Node.js for asset checks, Docker and Compose v2. From repository root:
```bash
git status
git switch -c development/your-change
dotnet restore ChatPlatform.csproj
dotnet build ChatPlatform.csproj -c Release --no-restore
dotnet list ChatPlatform.csproj package --vulnerable --include-transitive
python3 tests/check_web_assets.py
dotnet run --project tests/MessagingSmoke/MessagingSmoke.csproj -c Release
python3 tests/tenant_directory_scale.py 1000000
docker compose config --quiet
docker build -t chatplatform:local .
git diff --check
```
Run HTTP smoke and backup tests against a disposable app database only, using the exact environment/setup in .github/workflows/isolated-verification.yml:
```bash
export CHATPLATFORM_DATA_DIR="$(mktemp -d)"
export ConnectionStrings__DefaultConnection="Data Source=$CHATPLATFORM_DATA_DIR/platform.db"
export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS=http://127.0.0.1:5080
export WHATSAPP_VERIFY_TOKEN=ci-only-test-token
export WHATSAPP_APP_SECRET=ci-only-test-secret
export Campaigns__Enabled=true
eval "$(python3 tests/security_smoke.py --environment)"
dotnet run --project ChatPlatform.csproj --no-launch-profile
```
Follow the workflow's synthetic legacy seed and bootstrap settings for the full suite; the snippet alone is not the complete fixture. In a second terminal with identical environment run the workflow smoke and backup commands. Never aim tests at Fly production. Review vulnerability report; success of the report step alone is not a vulnerability gate.
For production diagnosis use scripts/check_deployment.py and /health + /ready; verify revision, secrets, storage and backup. No real Meta requests in mocked tests.

## 5. Extend an external database / إضافة محول
Implement IDatabaseAdapter in Infrastructure/Adapters/Databases with a unique provider name, read-only connection and transaction. Register with AddSingleton<IDatabaseAdapter, YourAdapter>() in Program.cs. No provider switch in registry; new providers need no edits to existing adapters. Configure server-only tenant aliases and named queries with @input, using environment or secret manager. Verify TLS and least-privilege DB role; use trusted network egress and dedicated read-only files. Never accept arbitrary customer SQL, passwords in code, connection URLs from a bot or cross-tenant aliases. Add provider contract, parallel reads, injection, write denial, timeout, tenant isolation, cancellation and actual server tests. The registry caps 16 concurrent operations per process and disables pooling; no distributed quota claim.

## 6. Extend bot and channel functionality / توسيع البوتات
Validate each node/edge, preserve existing JSON defaults, ensure simulator and runtime agree, maintain bounded execution, state by tenant/conversation/activation and idempotent outbox. Update designer, tests and both help languages with any new node. Currently one active release per tenant, 40 nodes, eight captured variables; external actions are not implemented. Future external actions require explicit permissions, trusted mappings, bounded execution, auditing and replay-safe idempotency. Do not call remote databases while holding a long SQLite write transaction. New channel adapters must normalize verified ownership, signatures and event keys; keep provider policies separate from core logic.

## 7. Database changes / تعديل التخزين
Do not change the existing primary provider as part of external integration. Add migrations idempotently, preserve data and keys, test twice on legacy fixture, verify backup and restore before production. Composite indexes must match query predicates; use cursor pagination instead of deep offsets. Keep slow CPU work outside write transactions where safe. SQLite single-volume architecture is a production scaling limitation. For cross-store effects use outbox/idempotent operations, not assumed distributed atomic commits.

## 8. Deployment and rollback / النشر والاستعادة
1. Isolated branch + PR; review diff, permissions, migrations and CI.
2. Verify backup using scripts/database_recovery.py (see HELP.md recovery commands), store encrypted off-server copy, retain secret configuration separately.
3. Confirm exact main revision, Fly app and volume. Inspect deployment workflow triggers; no deployment marker or dispatch without explicit approval.
4. Verify /health revision and /ready after authorized deployment, real Meta test-number traffic, inbox/reply/receipt, tenant separation and logs. If mismatch, stop rollout and investigate.
5. Restore offline into a new path, revoke old sessions, retain encryption keys, reconcile queues and ambiguous delivery before resuming workers. Never overwrite live DB or replay pending sends blindly.

## 9. Performance and acceptance / الأداء
Reference CI 46058912602 ran the actual million-record synthetic directory benchmark: insert 1M tenant rows + 1M admin mappings 3.15 s; p95 deep keyset 0.018 ms, exact ID 0.005 ms, exact name 0.006 ms, admin lookup 0.017 ms on the runner. These are warm indexed SQL microbenchmark results, not authenticated API or onboarding throughput. Verify separately full registration (password hashing, subscriptions, audit), account linkage, real traffic, worker fairness, queue lag, duplicate handling, sustained load, fault recovery, retention and Meta limits. External adapters do not solve primary SQLite write contention. See SCALABILITY.md.

## 10. Code comments and contribution standard / توثيق الكود
Comment each meaningful declaration, statement or logical step where it clarifies purpose, inputs, invariants, failures, tenant/security scope, transactions, timing and external side effects. Do not insert comments inside SQL/JSON strings, expressions, generated files or HTML/JS templates where they change behavior. Document syntax-only braces/continuations as part of their parent statement. Use XML summaries on public contracts, explain why not merely restate syntax, and add tests for every behavior change. Bilingual help must be maintained together. See SOURCE-COMMENT-COVERAGE.md for the current inline-comment scope; do not treat the whole repository as fully annotated.

## Business integration adapter extension
See [BUSINESS-INTEGRATIONS.md](BUSINESS-INTEGRATIONS.md) for IBusinessReadAdapter, the typed money/status contract, operator provisioning, the external SQL adapter and HTTPS gateway. Keep calls outside core write transactions; no arbitrary SQL, endpoints, financial writes, or bot disclosure without explicit identity/permission design. Test real vendor semantics in sandbox before launch.

Business request node: BotFlow stays pure, runtime records source/operation with human handoff, no external I/O during inbox transaction; BotDraftsController validates configured source on publish. ConversationAutomation stores nullable metadata; admin request list is tenant-scoped. Explicit return to bot clears it. The isolated HTTP workflow configures a disposable external ERP fixture for signed-webhook and read tests. Customer verification and external write/settlement remain separate release gates.

CustomerBusinessIdentityController adds short-lived admin-attested encrypted reference bindings. Scope by tenant/conversation/source/operation, check active channel, reject unconfigured operations, recheck revision and expiry after external I/O. No customer authentication or bot disclosure yet; retain data protection keys, rehearse additive migration and add retention/durable audit before commercial self-service.
