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

## Phase 4: commercial platform development
Baseline: archive/security-baseline-20261121 at 680db70682c9377a176f0e715d04669629bd9fe4 (CI success). Development continues separately.
- [x] Add tenant-managed Agent accounts, password reset, activation and session revocation (CI passed at 261f3d3).
- [ ] Verify the new team UI in a real browser, and implement a first-login password change flow.
- [ ] Backup and restore drill on disposable SQLite; verify Docker runtime and production configuration.
- [x] CRM contacts and shared inbox assignment, tags, search and pagination (candidate; CI pending).
- [ ] CRM activity history, reporting, media, template composer, live updates and browser accessibility tests.
- [ ] Reviewed OAuth signup and real-time tenant groups; leave legacy unrestricted endpoints denied.
- [ ] Subscriptions, quotas, billing and operational monitoring.
Do not merge or deploy until reviewed, tested and explicitly approved.

- Media: candidate upload/download/preview and audio-file playback; automatic 5-second refresh; direct voice recording, permanent retention, malware scanning, positive mocked media tests and true realtime push remain pending.

- [x] Authenticated inbox SSE invalidation stream and visual bot draft editor/simulator (candidate; CI pending).
- [ ] Browser drag/touch validation, reconnect load test, bot publication/activation, durable execution, event deduplication and quotas.

- [x] Published immutable bot snapshots, tenant-exclusive durable automation, legacy fallback and execution history (candidate; CI pending).
- [ ] Stateful bot conversations, handoff nodes, scheduled actions, platform quotas and external delivery ambiguity review.

## Phase 5: Owner's AI and professional design
- [x] Local tenant knowledge CRUD/search and disabled-by-default owner-model preview contract (candidate).
- [ ] Professional unified Arabic RTL UI, accessibility/mobile browser tests.
- [ ] Multi-turn flow state, human handoff, model integration tests, explicit AI reply opt-in, knowledge retrieval quality.
- [ ] Dependency security remediation, backup/restore, production configuration before release.

- [x] Unified design foundation, authenticated dashboard and knowledge-management UI (candidate; browser validation pending).

- [x] Multi-turn wait nodes, persistent cursor, human handoff and resume controls (candidate, CI pending).
