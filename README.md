# ChatPlatform

Multi-tenant WhatsApp Business automation platform built on ASP.NET Core 8, Entity Framework Core, SQLite and a shared Arabic web workspace.

## Documentation
- [Arabic user guide](docs/HELP-AR.md)
- [English user guide](docs/HELP-EN.md)
- [Developer and maintainer guide](docs/DEVELOPMENT.md)
- [Scalability and external database adapters](docs/SCALABILITY.md)
- [Source-comment coverage](docs/SOURCE-COMMENT-COVERAGE.md)
- [Historical operational commands and phase notes](HELP.md) — use current guides for feature status.

## Architecture
The existing modular application uses server-derived tenant identity, role authorization, a durable WhatsApp inbox/outbox and receipt workers. Visual bots are validated and versioned; inbound automation is staged transactionally with the inbound message. A process-local event bus supports supplemental adapters. External database providers implement IDatabaseAdapter and are registered independently; the primary platform database stays unchanged. Optional owner-model preview is disabled by default and has no mandatory paid provider.

## Development
Use Git, .NET SDK 8, Python 3, Node.js and Docker/Compose v2. See the developer guide for isolated local setup. Basic checks:
```bash
dotnet restore ChatPlatform.csproj
dotnet build ChatPlatform.csproj -c Release --no-restore
python3 tests/check_web_assets.py
dotnet run --project tests/MessagingSmoke/MessagingSmoke.csproj -c Release
python3 tests/tenant_directory_scale.py 1000000
```
Run HTTP and recovery smoke tests only using the disposable environment in .github/workflows/isolated-verification.yml. Do not commit real credentials or customer data.

## Release status
Reference development revision 15d23313 passed isolated CI [run 247](https://github.com/moknethunter/ChatPlatform/actions/runs/46058912602), including build, tenant/security smoke, mocked Graph, container health and a million-row synthetic institution directory benchmark. The million-institution target is not certified production throughput. Live Meta integration, mobile-browser acceptance, external PostgreSQL verification, full-scale concurrent provisioning/traffic and production recovery remain separate gates. No production database migration or deployment without authorization; verify the exact deployed revision and restorable backup first.

## Contribution
Preserve the core architecture and tenant isolation. Add integrations via adapters, keep existing definitions compatible, document both languages and add tests. Use development branches and reviewed PRs. Consult the developer guide before making schema or deployment changes.
