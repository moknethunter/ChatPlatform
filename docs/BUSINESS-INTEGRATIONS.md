# Business integrations / تكامل الأنظمة المحاسبية والدفع وقواعد البيانات

## Scope / النطاق
The primary platform architecture and SQLite database remain unchanged. Two independent read adapters expose customer balances, invoices and payment status through server-provisioned tenant sources. The database adapter reuses IDatabaseAdapter / ExternalDatabaseRegistry; the HTTPS gateway uses an explicit provider-neutral protocol. No specific vendor contract, live credentials, or merchant approval was supplied. Therefore this is not a direct integration with any named payment provider, and it cannot charge, refund, transfer or create invoices. Admin-only UI: /integrations.html. Bot designer supports a terminal business-request node that hands the conversation to a human; no public financial lookup or disclosure until verified customer identity and per-operation authorization are implemented.

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
Implement IBusinessReadAdapter with a unique provider key and register via DI. Add provider configuration validation and tests; never let a user select an unapproved endpoint. For a real vendor, map canonical operations and result semantics to the official API, test with sandbox data, and validate currency/reference/status. Financial writes need a separate design with explicit user approval, merchant scope, idempotency, signed verified callbacks, reconciliation, audit and recovery. Do not add payment-card/PIN collection. Automatic bot integration needs verified customer identity and policy-bound lookups; do not expose unrestricted accounting queries to WhatsApp users.

## Tests
Isolated MessagingSmoke tests exercise real SQLite reads and a fake HTTPS handler, forbidden cross-tenant/operation requests, invalid references, response mismatch and oversized response. HTTP security smoke tests check Admin-only access, CSRF and unconfigured denial. These are automated tests, not proof of live merchant/provider integration.

## Visual bot integration / طلب استعلام من البوت
Add the terminal Business request node, select a configured source and operation, simulate (offline only), save and publish. Publication rejects an unconfigured/foreign source or unavailable operation. A signed inbound message reaching this node atomically records the request and moves its conversation to human mode. No external call occurs in the inbound transaction, and the node never returns balances or payment data to WhatsApp. The request contains only source/operation and conversation metadata; it does not collect or persist a financial reference from customer text. The integration workspace lists the latest 100 requests for the authenticated tenant Admin. The admin must independently verify customer identity and authorization and explicitly enter the correct reference to read the system. Opening the conversation is for verification, not authorization proof. Return the conversation to bot mode to close the request. No automatic financial replies, charges, settlement, or invoice writes are provided. Schema additions are nullable and idempotent; backup before production migration.
CI provisions a disposable ERP invoice database and tests a signed inbound request, tenant-scoped list, explicit admin read, denied foreign source/publication, no automatic response and request closure. No live merchant requests.

## Read-only payment comparison
Configure explicit same-merchant source pairs at PaymentReconciliation:Tenants:<tenant>:<pair>:InvoiceSource and PaymentSource. The payment adapter may return optional invoiceReference, derived from trusted provider data, not user input. GET /api/payment-reconciliation lists only owned pairs; POST /api/payment-reconciliation/{id}/compare takes invoiceReference and paymentReference. The service reads both sources in parallel with bounded time, requires exact provider-attested invoice linkage, currency, successful payment and equal positive amount. Partial/void invoices require review. matched_for_review is NOT settlement: no invoice write, charge, release of goods, or automatic ledger update. Real provider mapping, merchant identity and independent ledger reconciliation are required before commercial use. No configuration by default.

## Staff-attested customer/record verification
For a pending business request, an authenticated tenant Admin independently verifies identity and entitlement to the exact record using the institution's trusted records or approved verification process. A WhatsApp number or customer-entered reference alone is not proof. POST /api/business-verification/{conversationId}/grant requires reference, independentlyVerified=true, source, operation and requestUpdatedAtUtc matching the current request. It issues a five-minute grant scoped to tenant, conversation, source, operation, admin and session. Only an encrypted reference and hashed session identifier are stored. POST /api/business-verification/{conversationId}/read takes grantId, atomically consumes it before external I/O and rechecks the current request before returning data. DELETE /api/business-verification/{conversationId}/grant revokes the pending grant; returning to bot mode deletes it. A remote failure requires a new attestation. Expired grants cannot be used, but background retention cleanup remains pending; replacement/closure/revocation remove them. Revocation cannot undo a read already completed. No financial data is automatically sent to WhatsApp. This is an administrative attestation mechanism, not automated customer authentication, permanent account linkage, or payment authorization. Existing general admin read API is deliberately unchanged; automatic bot lookups require independent trusted customer identity/entitlement evidence, provider integration and disclosure policy.
