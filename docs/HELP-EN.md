# ChatPlatform user and operator help
[العربية](HELP-AR.md) | [Developer guide](DEVELOPMENT.md) | [Scalability](SCALABILITY.md)

Reviewed against the development branch on 27 November 2026; the deployed Fly.io revision may differ. ChatPlatform uses ASP.NET Core 8, SQLite and an Arabic web workspace. Never put credentials or customer data in GitHub or chat.

## 1. Sign in and roles
Open /login.html on your HTTPS service and use the account issued by your administrator. There is no default password. SuperAdmin uses /platform-admin.html for plans, tenant provisioning, subscriptions, suspension, administrator reset and audit; direct tenant conversation reading is not permitted. Admin manages the organization's WhatsApp, bots, knowledge, marketing, team and subscription. Agent works within the authorized tenant conversation/customer workspace. Assignment is routing metadata, not a separate conversation-read boundary inside a tenant. Sign out to revoke the session. Owner MFA is still a commercial-release requirement.

## 2. Provision and manage an organization
Create a plan with limits for messages, employees, accounts, bots and articles and optional features. Create the organization, a unique administrator username (3–64 lowercase ASCII letters, digits or ._-), a strong initial password (at least 14 characters) and a valid subscription expiry. Create agents from /team.html as tenant Admin.
The owner directory supports exact ID, full name and administrator username searches and cursor pages of up to 100 records. Change subscriptions or suspend a tenant as needed. Suspension revokes sessions and cancels pending sends without deleting records. In-flight requests cannot be recalled. Pricing is administrative; automatic billing is not implemented.

## 3. WhatsApp
Use manual linking on /whatsapp.html: WABA ID, Phone Number ID and valid access token. An app ID is not a WABA ID. Server verifies ownership and protects stored tokens. Configure the webhook verification token and app secret server-side.
Run stored-account verification and readiness checks. Membership and app subscription are not delivery evidence. Use an allowed test recipient and the existing Meta test number, receive a signed inbound message, verify inbox visibility, send a reply and inspect receipts. If linking fails, check the token, WABA membership, messages subscription and webhook configuration. Embedded Signup is disabled by default; do not enable until Meta approval and real end-to-end validation.

## 4. Workspace
| Page | Purpose |
|---|---|
| /index.html | Tenant dashboard |
| /inbox.html | Shared inbox, search, filters, replies, assignment, status, unread and human handoff |
| /contacts.html | CRM profiles, stages, tags, notes and recent activity |
| /bots.html | Visual bot drafts, simulation and publication |
| /knowledge.html | Articles, local search and optional owner-model preview |
| /marketing.html | Customer lists, consent, segments and campaigns |
| /reports.html | Tenant reports |
| /subscription.html | Entitlement usage and expiry |
| /team.html | Managed agents |
Free-form replies require an eligible WhatsApp service window; outside it use approved templates where applicable. Queued is not Sent; Sent is not Delivered or Read. DeliveryUnknown requires reconciliation before resending. Supported attachments include JPEG/PNG, MP4, PDF, MP3/Ogg up to 5 MiB. Audio recording depends on browser support. Permanent media archiving and comprehensive malware scanning are not implemented.

## 5. Visual bots
Create a draft, drag or select nodes, link next/yes/no paths, simulate, save and explicitly publish the saved version. Available types: start, contains, reply, wait, capture, handoff and end. Text modes: contains, equals and starts_with.
Example: start → wait "What is your name?" → capture name → reply "Hello {{name}}" → end. Multi-turn simulation uses one message per line and does not dispatch. Limits: 40 nodes, eight distinct capture names, 500 characters per value, 30-minute waiting-state expiry. Do not capture passwords or payment details. Completion, handoff or activation changes prevent reuse of previous waiting context; expired data may remain stored until updated. One active published flow per tenant. Stopping affects future messages; human takeover cancels pending automated replies but not in-flight requests.

## 6. Knowledge and campaigns
Publish articles for local lexical search. Owner-model preview is optional, manual and disabled by default; no mandatory paid AI or automatic model-generated WhatsApp replies.
Choose a sending account and verify explicit consent for that account and an approved template. Preview a segment and explicitly save a list of up to 100 selected recipients. Preview does not send or grant consent. Lists are snapshots. Consent is checked at scheduling and dispatch; STOP revokes consent and cancels eligible pending sends. Campaigns are disabled by default.

## 7. External databases
The primary platform database remains unchanged. Operators configure independent tenant-owned aliases using IDatabaseAdapter implementations for SQLite and PostgreSQL. Current access is read-only through approved parameterized operations; no arbitrary SQL, writes, automatic synchronization or bot action node. Tenant admins may list aliases with GET /api/database-connections and probe via POST /api/database-connections/{id}/probe. Secrets are never returned. UI and real PostgreSQL server tests remain outstanding. See SCALABILITY.md.

## 8. Troubleshooting and safe operation
401: sign in again; 403: verify role and tenant scope; 409: check conflict, quota or feature/configuration state; 503: temporary service or connectivity problem. /health exposes service revision and /ready checks storage connectivity, not WhatsApp delivery. Reconcile ambiguous sends rather than blindly retrying. Never delete /data. Back up and rehearse recovery before production migrations; keep keys and secret configuration private. Verify deployed revision, real Meta delivery, access control and recovery before production approval.

## 9. Verified status
Isolated CI run 46058912602 (run 247, reference 15d23313) passed build, Docker, mocked Graph, tenant isolation and the one-million synthetic tenant/admin directory benchmark. It does not certify a million active organizations, real Meta accounts, mobile UX, sustained load, fair scheduling or commercial disaster recovery. See the developer guide for reproducible commands and release gates.

## Accounting and payment integrations
Tenant Admins can open /integrations.html to list configured sources, query a balance, invoice or payment status, and probe external databases. Read-only, no charges or accounting writes. Operator provisioning and vendor-specific sandbox validation are required. See [business integrations](BUSINESS-INTEGRATIONS.md). Bot actions remain unavailable.
