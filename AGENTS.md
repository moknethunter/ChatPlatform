# ChatPlatform engineering instructions

## Mandatory safety gates
- Work on a dedicated branch from main. Never deploy or merge without explicit approval and successful checks.
- Use disposable databases and fake credentials in tests; never send real WhatsApp messages in CI.
- Do not trust tenant IDs from headers, URLs or bodies. Derive tenant and role from a verified identity and enforce ownership in every query and mutation.
- Public routes must be explicitly reviewed (health, legal pages, signed Meta webhook).
- Never use a shared API key as end-user identity. Protect SignalR and background jobs as well as HTTP.
- Document commands with purpose, prerequisites and expected output in HELP.md. Add comments explaining non-obvious security decisions, not redundant syntax.
- Report actual test output, failures and skipped checks. No claims of production readiness without evidence.

## Work order
1. Infrastructure and reproducible CI.
2. Authentication, authorization, secret handling and security regression tests.
3. Tenant isolation, role matrix and cross-tenant negative tests.
See HELP.md and ROADMAP.md.

## Owner-model constraint (Phase 5)
- AI inference must use the owner's future self-hosted model, via the versioned project protocol. No paid model API integrations, provider SDKs, external AI fallbacks or required API subscriptions. Keep model calls disabled until an operator explicitly configures the owner's endpoint; knowledge search and ordinary bots work offline. Do not send real customer data to an unverified model. Any automatic AI replies require separate review and explicit enablement.
