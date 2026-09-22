# Contributing to NodePilot

This guide is written for **human contributors**. It covers local setup, the build and test loop, as well as the conventions a change must follow before it merges. The `CLAUDE.md` runbook at the repository root is written for AI agents. It carries deeper architectural detail but assumes an autonomous workflow. This document is the one for onboarding, with `CONTEXT.md` and `docs/` for domain depth.

> **License:** NodePilot is licensed under the [Apache License 2.0](LICENSE). Contributions are licensed under the same terms.

## Prerequisites

- **.NET 10 SDK**: the solution targets `net10.0-windows`, so a Windows host is required. The remote-execution and PowerShell-SDK layers are Windows-only. The accepted SDK band is pinned in [`global.json`](global.json).
- **Node.js** and npm, for the frontend as well as the docs site. The minimum version is declared in the `engines` field of each `package.json`, react-router 8 sets the floor, and `npm` warns below it. The number is deliberately not hard-coded here, because a hard-coded copy drifts from the manifests.
- **PostgreSQL 16+** for running the backend locally. SQL Server 2022 CU1+ is the alternative provider. SQLite is used only as the in-memory test backend.

## Local setup

**1. Create the database.** Neither shipped connection string carries a password, so this is a required step rather than a formality:

```powershell
winget install PostgreSQL.PostgreSQL
$psql = "C:\Program Files\PostgreSQL\16\bin\psql.exe"
& $psql -U postgres -c "CREATE ROLE nodepilot WITH LOGIN PASSWORD 'ChangeMe!';"
& $psql -U postgres -c "CREATE DATABASE nodepilot OWNER nodepilot;"
```

Any reachable PostgreSQL works, a service install, a container, or a hand-rolled cluster started with `pg_ctl`. Nothing in this repository provisions one.

**2. Run it.** The password is passed through the environment so that it never lands in a tracked file:

```powershell
# Backend on http://localhost:5000 (the port launchSettings binds and the Vite proxy targets)
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5432;Database=nodepilot;Username=nodepilot;Password=ChangeMe!;SSL Mode=Disable"
cd src\NodePilot.Api; dotnet run

# Frontend on http://localhost:5173 (proxies /api, /healthz and /hubs to the backend)
cd src\nodepilot-ui; npm install; npm run dev
```

PostgreSQL is started **before** the API. Without a reachable database the process exits during the migration bootstrap, naming the server and database it could not reach.

**3. First login.** An empty database does **not** simply accept the first login. The API writes a one-time setup token to `src\NodePilot.Api\admin-setup.token`, in the content root. Sign in with the admin username and password you want. The login screen reveals a **Setup token** field on the first attempt, and pasting the token creates the Admin account.

> Local logins are fully enabled in Development. In Production `Authentication:LocalLoginMode` defaults to `BreakGlassOnly`, where only accounts explicitly flagged as break-glass may sign in with a password.

The AI Chat's source-code knowledge source can be pointed at your checkout. Set it outside version control (`$env:AiKnowledge__SourceCodeRootPath = 'C:\path\to\NodePilot'`) never in `appsettings.Development.json`, which is tracked and shared.

## Build & test

Every change must build clean and keep the suites green.

```bash
dotnet build                                    # backend (Central Package Management, see below)
dotnet test                                     # all backend suites (xUnit)
cd src/nodepilot-ui && npm run build            # frontend type-check + build
cd src/nodepilot-ui && npm run test:run         # frontend unit tests (vitest)
cd src/nodepilot-ui && npm run lint:ci          # frontend lint (warning-capped, see below)
cd src/nodepilot-ui && npm run test:e2e         # hermetic Playwright e2e (no backend needed)
cd src/nodepilot-docs-ui && npm run build       # documentation website type-check + build
cd src/nodepilot-docs-ui && npm run build:site  # project website build
cd src/nodepilot-docs-ui && npm run test:run    # docs-site tests incl. the de/en parity guard
```

### Database provider integration tests

The pre-production migration history was consolidated into `20260915180058_InitialBaseline`. It creates the complete current schema, including the filtered Running-step index. Databases created by the previous history are not upgrade targets for this baseline. A new development database is used instead, or required data is exported and imported explicitly. Startup does not reset existing databases or rewrite their migration history. Future schema changes append migrations after this baseline.

Schema and locking changes must also run against real PostgreSQL and SQL Server. From the repository root on Windows, with Docker using Linux containers:

```powershell
./scripts/Test-DatabaseProviders.ps1
```

The runner starts disposable PostgreSQL 16 and SQL Server 2022 containers on loopback ports, runs the `DatabaseIntegration` tests and removes those containers afterward. Each fixture creates its own `nodepilot_test_<guid>` database. Coverage includes fresh migrations and upgrades, atomic outbox claims, SQL Server with RCSI on and off, as well as lease-fenced recovery. SQLite remains the fast default for ordinary database tests. It does not validate either production provider's locking behavior.

To use existing **test servers**, set both `NODEPILOT_TEST_POSTGRES` and `NODEPILOT_TEST_SQLSERVER` to administrative connection strings in the local environment. Their accounts must be able to create and drop test databases. SQL Server tests also change RCSI on those disposable databases. The runner then leaves the servers themselves running. These credentials belong outside tracked files. `-NoBuild` applies after the test projects have been built, and `-Filter 'FullyQualifiedName~ExecutionDispatchOutboxClaimerTests'` selects a focused scenario. Should a test server not be configured, the real-provider tests report a skip.

With `NODEPILOT_TEST_POSTGRES` configured, the dispatch comparison can be repeated separately:

```powershell
dotnet test tests/NodePilot.Data.Tests --filter 'FullyQualifiedName~DispatchClaimBenchmarkTests' --logger 'console;verbosity=detailed'
```

It compares the historical SELECT/compare-and-set claim loop with atomic claims and shared idle polling: 20 workers, a 3.25-second idle window, then 100 queued starts with 25 ms of simulated work. Both paths use the same coalesced signals and PostgreSQL connection pooling with a 40-connection cap. Output reports claim statements per start and burst-release-to-claim p50/p95. Setup and completion deletes are excluded from the statement count. This isolates the claim protocol and does not model full workflow execution or remote activities. It is run without other database load.

### Documentation website

`src/nodepilot-docs-ui` is a standalone Vite SPA. `.github/workflows/docs-pages.yml` publishes it to GitHub Pages at [sev7enup.github.io/NodePilot/docs](https://sev7enup.github.io/NodePilot/docs/) on every push to `main` that touches the package. `npm run dev` serves it locally on port 5174, under `/docs/`.

The same package holds the **project website** in `src/site/`: plain TypeScript without React or Tailwind, in English and German, built by its own Vite config (`vite.site.config.ts`) into `dist-site/`. The same workflow publishes it at the root of the Pages site, [sev7enup.github.io/NodePilot](https://sev7enup.github.io/NodePilot/), and also runs when the screenshots in `docs/images/` change, because the website bundles some of them.

- `npm run dev:site` serves the website alone on port 5175, enough for work on the website itself.
- `npm run build:site` builds it into `dist-site/`.
- `npm run build:demo` builds the browser demo in the neighbouring package (`src/nodepilot-ui` into `dist-demo/`).
- `npm run assemble:site` runs `scripts/assemble-site.mjs`: from the three finished builds it assembles `_site/` exactly as the workflow publishes it, the website at the root, the docs in `docs/`, the demo in `demo/`, `pages-media/` in `media/`. Every input is required, so a missing build fails the assembly instead of publishing a site that keeps the previous demo.
- `npm run preview:site` runs all three builds and the assembly, then serves `_site/` on port 5175. It is the only complete local preview, since `docs/`, `demo/` and `media/` exist only in the assembled `_site/`.

Old documentation links without `/docs/` (`#/en/deployment/logs` at the site root) keep working: `src/site/public/legacy-docs-redirect.js` forwards every hash that is not a website route to `docs/`. A new top-level website route therefore has to be added to that script and to `SITE_ROUTE_SEGMENTS` in `src/site/router.ts`, and it must not collide with a docs path or language. The site tests check both.

The docs have a **second deployment**: `deploy/Build-Artifact.ps1` and `deploy/desktop/Build-DesktopInstaller.ps1` build them as well and stage them into `wwwroot/docs`, which the API serves at `/docs` so that a disconnected installation has the runbooks. The website is not part of that and never reaches `dist/` or the product. `-SkipFrontend` skips both npm builds, `-SkipNpmCi` applies to both. This has two consequences for anyone editing the docs: `index.html` must not contain an inline `<script>`, because the API serves it under `script-src 'self'` and `src/lib/document-head.test.ts` guards it, and the Vite base stays relative so that the bundle works under a subdirectory. The main UI's dev server proxies `/docs` to the docs dev server on port 5174, so its help button reaches the docs while both run.

The docs site ships **its own curated markdown corpus** under `content/`. It does not render `docs/`, which means that a change to `docs/` reaches the site only if it is mirrored deliberately.

**The docs site is bilingual, and that is machine-enforced.** Every page exists twice: `content/de/<path>.md` and `content/en/<path>.md`, with both trees on exactly the same set of paths. A new page therefore needs both files plus a title in **both** `src/i18n/locales/de.json` and `en.json`. Adding only one language fails `src/lib/content.test.ts` in CI, which is the point: the two half-states fail differently and neither is obvious to whoever wrote the page. An English-only page quietly serves English to German readers under a "not translated yet" notice, while a German-only page 404s for everyone else, since the fallback resolves to English and there is none.

Two things are worth knowing when writing content:

- **Cross-links carry no language prefix.** `../enterprise/folder-rbac` is the correct form. The active language is prepended at runtime, and that is what keeps a reader inside their language while clicking.
- **Only `content/en/` feeds the AI knowledge assistant** (wired in `NodePilot.Api.csproj`), so an English page is what the in-product assistant will quote.

**While iterating, the run is scoped to the change.** The full suites above are what CI executes on every pull request, and repeating them locally on each edit buys no extra signal:

```bash
dotnet test tests/NodePilot.Engine.Tests --filter "FullyQualifiedName~WorkflowCallGraphBuilder"
cd src/nodepilot-ui && npx vitest run src/__tests__/lib/opsTimeline.test.ts
cd src/nodepilot-ui && npx playwright test e2e/operations.spec.ts --config=playwright.dev.config.ts
```

The full suites belong before a release cut, after a dependency bump, or for a project-wide refactor. When something is touched that a guard or parity test watches (activity catalog, API DTOs, migrations, audit codes, trigger keys, settings schema) that specific test is run as well. The mapping is in [`CLAUDE.md`](CLAUDE.md) under *Build & Test*.

- **Package versions** are centralized in `Directory.Packages.props` under Central Package Management. A dependency is added by referencing it version-less in the csproj and adding a `<PackageVersion>` entry centrally. Shared build settings live in `Directory.Build.props`.
- **Lint is ratcheted:** `npm run lint:ci` fails on any *new* warning above the documented floor. Warnings are cleared rather than added, or the cap is lowered.

## Conventions

- **Tests are mandatory.** Every behavioral change ships with matching tests in the same PR. Naming follows `MethodName_Scenario_ExpectedResult`. The remote and WinRM layer is always mocked. DB tests normally use in-memory SQLite. Production-provider schema and concurrency checks use the isolated integration runner above. Coverage gates: backend ≥ 85 % line / ≥ 70 % branch, enforced in `.github/workflows/ci.yml`, which is the single authoritative number, and frontend per `vitest.config.ts`.
- **Models and interfaces live in `NodePilot.Core`**, which has no project dependencies.
- **i18n:** every user-visible string goes through `react-i18next` in **both** `de` and `en` locale files. The default UI language is German. The documentation website is bilingual as well, including its markdown corpus, see [Documentation website](#documentation-website).
- **Architecture tests are load-bearing.** Several guard tests keep cross-boundary mirrors in sync: activity and alerting catalogs, admin-settings sections, Cli/Mcp DTO parity, RBAC as well as audit coverage. Should one fail, the drift is fixed rather than the guard weakened.
- **No backward-compat shims.** NodePilot is greenfield: schema changes go through EF migrations, and old code paths are removed outright rather than kept behind flags.
- The surrounding code's style, comment density and idioms are matched.

## Commit & PR process

1. Branch off `main`, never commit directly to `main`.
2. Keep commits focused. Write clear messages describing the *why*.
3. Open a PR using the template. CI must be green: backend build and test, frontend lint/build/test, docs-ui build, desktop typecheck and test, e2e.
4. Architectural decisions of lasting consequence get an ADR under `docs/adr/`, see [`docs/adr/README.md`](docs/adr/README.md) for when one is warranted and for the template.

## Where to look

- `README.md`, feature overview, configuration reference, project layout.
- `CONTEXT.md`, domain glossary, the shared vocabulary the code uses.
- `docs/`: subsystem deep-dives for alerting, custom activities, MCP server and enterprise features.
- `docs/adr/`: architecture decision records.
