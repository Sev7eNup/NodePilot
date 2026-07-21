# Contributing to NodePilot

Thanks for your interest in NodePilot. This guide is for **human contributors** — it covers
local setup, the build/test loop, and the conventions a change must follow before it merges.
(The `CLAUDE.md` runbook at the repo root is written for AI agents; it has deeper architectural
detail but assumes an autonomous workflow. Prefer this document for onboarding, then reach for
`CONTEXT.md` and `docs/` for domain depth.)

> **License:** NodePilot is licensed under the [Apache License 2.0](LICENSE). By contributing you agree
> your contributions are licensed under the same terms.

## Prerequisites

- **.NET 10 SDK** (the solution targets `net10.0-windows` — a Windows host is required; the
  remote-execution and PowerShell-SDK layers are Windows-only).
- **Node.js 20+** and npm (frontend + docs site).
- **PostgreSQL** for running the backend locally (a local dev cluster is fine). SQL Server is
  the alternative provider; SQLite is used only as the in-memory test backend.

## Local setup

```powershell
# 1. Start Postgres (example uses a local dev cluster)
& 'C:\NodePilot-Postgres\pgsql\bin\pg_ctl.exe' start -D 'C:\NodePilot-Postgres\data' -w

# 2. Backend on http://localhost:5000  (fails fast if Postgres isn't up)
cd src\NodePilot.Api; dotnet run --urls "http://localhost:5000"

# 3. Frontend on http://localhost:5173 (proxies /api to the backend)
cd src\nodepilot-ui; npm install; npm run dev
```

The first login on an empty database creates the initial Admin account.

## Build & test

Every change must build clean and keep the suites green.

```bash
dotnet build                                    # backend (Central Package Management — see below)
dotnet test                                     # all backend suites (xUnit)
cd src/nodepilot-ui && npm run build            # frontend type-check + build
cd src/nodepilot-ui && npm run test:run         # frontend unit tests (vitest)
cd src/nodepilot-ui && npm run lint:ci          # frontend lint (warning-capped — see below)
cd src/nodepilot-ui && npm run test:e2e         # hermetic Playwright e2e (no backend needed)
```

- **Package versions** are centralized in `Directory.Packages.props` (Central Package
  Management). Add a dependency by referencing it version-less in the csproj and adding a
  `<PackageVersion>` entry centrally. Shared build settings live in `Directory.Build.props`.
- **Lint is ratcheted:** `npm run lint:ci` fails on any *new* warning above the documented
  floor. Don't add warnings; clear them or lower the cap.

## Conventions

- **Tests are mandatory.** Every behavioral change ships with matching tests in the same PR.
  Naming: `MethodName_Scenario_ExpectedResult`. The remote/WinRM layer is always mocked; DB
  tests use in-memory SQLite. Coverage gates: backend ≥ 45 % line, frontend per `vitest.config.ts`.
- **Models and interfaces live in `NodePilot.Core`** (which has no project dependencies).
- **i18n:** every user-visible string goes through `react-i18next` in **both** `de` and `en`
  locale files. The default UI language is German.
- **Architecture tests are load-bearing.** Several guard tests keep cross-boundary mirrors in
  sync (activity/alerting catalogs, admin-settings sections, Cli/Mcp DTO parity, RBAC and audit
  coverage). If one fails, fix the drift — don't weaken the guard.
- **No backward-compat shims.** NodePilot is greenfield: schema changes go through EF migrations,
  and old code paths are removed outright rather than kept behind flags.
- Match the surrounding code's style, comment density, and idioms.

## Commit & PR process

1. Branch off `main` (never commit directly to `main`).
2. Keep commits focused; write clear messages describing the *why*.
3. Open a PR using the template. Ensure CI is green (backend build+test, frontend lint+build+test,
   docs-ui build, e2e).
4. Architectural decisions of lasting consequence get an ADR under `docs/adr/` — see
   [`docs/adr/README.md`](docs/adr/README.md) for when one is warranted and the template.

## Where to look

- `README.md` — feature overview, configuration reference, project layout.
- `CONTEXT.md` — domain glossary (the shared vocabulary the code uses).
- `docs/` — subsystem deep-dives (alerting, custom activities, MCP server, enterprise features…).
- `docs/adr/` — architecture decision records.
