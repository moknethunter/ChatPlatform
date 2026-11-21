# Security and engineering audit — 2026-11-21

Base commit: `d28d8095fe440c43b2f5b8744f1d733124bc18ce`. Initial static review of repository tree and selected implementation files; not a full penetration test or all-file semantic audit. No production requests made. Do not merge until verification and owner approval.

## Candidate changes
- Mask connector credential values in list responses; credentials still require authentication and secure storage.
- Reject non-object inbound/event payloads and non-string event types with HTTP 400.
- Correlate manual sends with their own outbox record instead of matching message text; save message and outbox in one transaction; distinguish verified duplicate from DB failure.
- Stop broadcasting internal platform events to every SignalR client; chat groups still require authenticated membership enforcement.
- Avoid writing a second error response after headers are sent or a client disconnects.
- Run isolated verification on development branches; reject deploy/image publish jobs on non-main refs; exclude local secrets and database files from git/Docker context. The ignore files do not scrub prior history.

## Open findings (static evidence)
| Priority | Location | Finding / required work |
| --- | --- | --- |
| Critical | Program.cs, Controlers/* | No configured user authentication or authorization pipeline. Implement verified identities, roles, session lifecycle and default-deny policies across UI/API. |
| Critical | ConversationsController, WhatsAppController, TenantsController | Caller-supplied tenant IDs and unrestricted reads/mutations. Derive tenant from verified identity and enforce all resource ownership. |
| Critical | EndpointMapper, MessagesController, WhatsAppController | Public send, template, connector and generic event routes can affect accounts; restrict to authenticated actors, validate schemas, rate limit, route sends through durable outbox, and isolate internal events. |
| High | ChatHub, ChatAdapter | Anonymous group join/send; candidate removes global fanout but does not enforce group membership. |
| High | ConnectorEntity, UniversalConnectorAdapter | Plaintext connector tokens and configurable outbound URL; encrypt secrets, redact errors, review SSRF restrictions and URL ownership. |
| High | WhatsAppOnboardingController | Review OAuth state, ownership, exchange logging and token replacement; secrets currently included in request URL in legacy code (verify compiled path). |
| High | Dockerfile | Runtime root and world-writable data directory; migrate file ownership safely and verify existing volumes before changing UID. |
| High | Program.cs | Forwarded header trust allows arbitrary proxies; restrict to verified ingress and validate host configuration before release. |
| High | InMemoryEventBus | Unbounded subscriber channels, ephemeral events; add limits and durable ingress for critical operations. |
| High | WhatsAppOutboxWorker | Review retry ambiguity (network timeout after remote acceptance) and error/log retention. |
| High | DataProtection setup | Keys share a database with encrypted account tokens; separate key protection and backup policy. |
| Medium | .github/workflows | Two image-publishing workflows push latest; consolidate and gate production deployment on required CI. |
| Medium | ChatPlatform.csproj | Dependencies pinned to early 8.0 releases; run NuGet advisory audit and verified patch upgrades, lock restore. |
| Medium | DbInitializer / SQLite | Require migration, restart, backup/restore and concurrent load tests before any DB/provider changes. |
| Medium | wwwroot/whatsapp.html | Dynamic innerHTML renders account/error content; audit interpolation and use textContent/DOM creation. |
| Medium | API list routes | Add bounded pagination and appropriate limits before scale testing. |

## Verification status
Static source inspection only at time of commit. Isolated CI is configured to run on this branch. Review actual build/test and dependency audit outputs; never report passes without results. Authentication/tenant isolation is still open and this branch is not production-ready.
