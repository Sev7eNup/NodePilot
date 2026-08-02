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
  remote-execution and PowerShell-SDK layers are Windows-only). The accepted SDK band is pinned
  in [`global.json`](global.json).
- **Node.js** and npm (frontend + docs site). The minimum version is declared in the `engines`
  field of each `package.json` — react-router 8 sets the floor, and `npm` warns if you are below
  it. Do not hard-code a number here; it drifted three different ways once already.
- **PostgreSQL 16+** for running the backend locally. SQL Server 2022 CU1+ is the alternative
  provider; SQLite is used only as the in-memory test backend.

## Local setup

**1. Create the database.** Neither shipped connection string carries a password, so this is a
required step, not a formality:

```powershell
winget install PostgreSQL.PostgreSQL
$psql = "C:\Program Files\PostgreSQL\16\bin\psql.exe"
& $psql -U postgres -c "CREATE ROLE nodepilot WITH LOGIN PASSWORD 'ChangeMe!';"
& $psql -U postgres -c "CREATE DATABASE nodepilot OWNER nodepilot;"
```

Any reachable PostgreSQL works — a service install, a container, or a hand-rolled cluster you
start with `pg_ctl`. Nothing in this repository provisions one for you.

**2. Run it.** Pass the password through the environment so it never lands in a tracked file:

```powershell
# Backend on http://localhost:5000 (the port launchSettings binds and the Vite proxy targets)
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5432;Database=nodepilot;Username=nodepilot;Password=ChangeMe!;SSL Mode=Disable"
cd src\NodePilot.Api; dotnet run

# Frontend on http://localhost:5173 (proxies /api, /healthz and /hubs to the backend)
cd src\nodepilot-ui; npm install; npm run dev
```

Start PostgreSQL **before** the API. Without a reachable database the process exits during the
migration bootstrap, naming the server and database it could not reach.

**3. First login.** An empty database does **not** simply accept the first login: the API writes a
one-time setup token to `src\NodePilot.Api\admin-setup.token` (the content root). Sign in with the
admin username and password you want — the login screen reveals a **Setup token** field on the
first attempt, and pasting the token creates the Admin account.

> Local logins are fully enabled in Development. In Production `Authentication:LocalLoginMode`
> defaults to `BreakGlassOnly`, where only accounts explicitly flagged as break-glass may sign in
> with a password.

Want the AI Chat's source-code knowledge source to work against your checkout? Set it outside
version control — `$env:AiKnowledge__SourceCodeRootPath = 'C:\path\to\NodePilot'` — never in
`appsettings.Development.json`, which is tracked and shared.

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
