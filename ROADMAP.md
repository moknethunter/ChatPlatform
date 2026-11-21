# Roadmap — phases 1–3

## Phase 1: infrastructure
- [x] Move Compose out of the workflow directory and align persistent SQLite path (candidate).
- [ ] Verify container runtime and restore a reliable container healthcheck.
- [ ] Pass CI; audit dependencies, migrations, backups and restore.

## Phase 2: security
- [x] Block development seed routes outside Development (candidate).
- [x] Remove hardcoded fallback from legacy middleware (candidate; middleware not yet activated).
- [ ] Implement authenticated identities, secure sessions, role-based policies, rate limiting and audit logs.
- [ ] Review all endpoint authorization, including SignalR, generic events, and sending.
- [ ] Test unauthenticated / forbidden requests and login/session lifecycle.

## Phase 3: tenant isolation
- [ ] Bind tenant to authenticated identity, not caller input.
- [ ] Scope reads/writes, account ownership, templates, conversations, messages, jobs and realtime groups.
- [ ] Prevent reassignment of another tenant's WhatsApp account without explicit authorized transfer.
- [ ] Test at least two tenant identities with positive same-tenant and negative cross-tenant access for every resource.

Current changes are candidates, not a completed security program. The existing API and UI require coordinated auth changes before activation, so avoid breaking production with a partial shared-key gate.
