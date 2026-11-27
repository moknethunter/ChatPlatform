# Business integrations / تكامل الأنظمة المحاسبية والدفع وقواعد البيانات

## Scope / النطاق
The primary platform architecture and SQLite database remain unchanged. Two independent read adapters expose customer balances, invoices and payment status through server-provisioned tenant sources. The database adapter reuses IDatabaseAdapter / ExternalDatabaseRegistry; the HTTPS gateway uses an explicit provider-neutral protocol. No specific vendor contract, live credentials, or merchant approval was supplied. Therefore this is not a direct integration with any named payment provider, and it cannot charge, refund, transfer or create invoices. Admin-only UI: /integrations.html. No public customer/bot execution until recipient identity and per-operation authorization are implemented.

## Setup (operator only)
Configure one or several aliases per tenant. Store connection strings and tokens in server secrets, never in GitHub. For SQL, use dedicated read-only roles/views and the existing adapter configuration. Each operation must return one JSON scalar or SQL NULL, with the exact typed result below. Example for a disposable SQLite database containing a table Customers(Id, BalanceMinor, Currency):
```text
ExternalDatabases__Tenants__tenant-a__erp__Provider=sqlite
ExternalDatabases__Tenants__tenant-a__erp__ConnectionName=ErpA
ConnectionStrings__ErpA=Data Source=/external/erp-a.db
ExternalDatabases__Tenants__tenant-a__erp__Reads__balance=SELECT json_object('reference',Id,'amountMinor',BalanceMinor,'currency',Currency,'status','available') FROM Customers WHERE Id=@input
BusinessIntegrations__Tenants__tenant-a__accounting__Provider=database
BusinessIntegrations__Tenants__tenant-a__accounting__DatabaseAlias=erp
BusinessIntegrations__Tenants__tenant-a__accounting__Operations__customer-balance=balance
BusinessIntegrations__Tenants__tenant-a__payments__Provider=https-gateway
BusinessIntegrations__Tenants__tenant-a__payments__Endpoint=https://your-approved-gateway.example/read
BusinessIntegrations__Tenants__tenant-a__payments__Token=<server-secret>
BusinessIntegrations__Tenants__tenant-a__payments__Operations__payment-status=status
```
For invoices map Operations__invoice to a separately approved SQL query or gateway action. The example is not a vendor schema. Use the actual vendor documentation, credentials and dedicated test environment before enabling. Configure a trusted HTTPS gateway and restrict network egress/DNS to its approved destination; endpoint string validation alone is not a DNS/SSRF firewall. Gateways must implement strictly read-only operations. Do not supply arbitrary customer-provided endpoints, SQL, operation names or credentials.

## Contract
POST to the configured gateway endpoint with Authorization: Bearer <server-secret> and JSON:
```json
{"protocol":"chatplatform.business.read.v1","operation":"status","reference":"pay-123"}
```
Expected JSON:
```json
{"reference":"pay-123","amountMinor":100,"currency":"YER","status":"succeeded"}
```
404 means no record. All other failures are sanitized. Response reference must exactly match input; amount is an integer minor-unit value (not floating point), currency three uppercase letters, and status must match operation:
- customer-balance: available (negative allowed).
- invoice: open, paid, void, overdue, partially_paid.
- payment-status: pending, succeeded, failed, cancelled, refunded.
Use the actual currency exponent; the UI deliberately displays minor units and does not assume all currencies have two decimals. The adapter result is a lookup from the configured source, not proof of settlement or a ledger reconciliation. Do not use it to automatically release goods or mark invoices paid.

## Platform API
GET /api/business-integrations returns only current-tenant aliases/provider/approved operations.
POST /api/business-integrations/{id}/read accepts {"operation":"payment-status","reference":"pay-123"}. Cookie-authenticated tenant Admin, CSRF header, 20 reads/minute per tenant per process; 16 concurrent reads per process, 10-second deadline, bounded 16 KiB gateway response. No automatic redirects/cookies/retries. 404 unavailable record/source/operation; 400 invalid input; 403 denied; 503 sanitized provider failure. Database catalog/probes use the existing API. Neither API exposes secret values or SQL. Configuration is currently static operator-managed; dynamic encrypted source management and distributed quotas are future work.

## Extend / التطوير
Implement IBusinessReadAdapter with a unique provider key and register via DI. Add provider configuration validation and tests; never let a user select an unapproved endpoint. For a real vendor, map canonical operations and result semantics to the official API, test with sandbox data, and validate currency/reference/status. Financial writes need a separate design with explicit user approval, merchant scope, idempotency, signed verified callbacks, reconciliation, audit and recovery. Do not add payment-card/PIN collection. Bot integration needs verified customer identity and policy-bound lookups; do not expose unrestricted accounting queries to WhatsApp users.

## Tests
Isolated MessagingSmoke tests exercise real SQLite reads and a fake HTTPS handler, forbidden cross-tenant/operation requests, invalid references, response mismatch and oversized response. HTTP security smoke tests check Admin-only access, CSRF and unconfigured denial. These are automated tests, not proof of live merchant/provider integration.
