# Contributing to NodePilot

This guide is written for human contributors and covers local setup, the build and test loop as well as the conventions a change has to follow. AI agents work from `CLAUDE.md`, while `CONTEXT.md` and `docs/` hold the domain depth.

> **License:** NodePilot is licensed under the [Apache License 2.0](LICENSE). Contributions are licensed under the same terms.

## Prerequisites

- **Windows** with the **.NET 10 SDK**, whose accepted band is pinned in [`global.json`](global.json). The solution targets `net10.0-windows`.
- **Node.js** and npm, at least in the version declared in the `engines` field of each `package.json`.
- **PostgreSQL 16+** as the default provider, or SQL Server 2022 CU1+. SQLite only serves as the in-memory test backend.

## Local setup

**1. Create the database.** Neither shipped connection string carries a password, so this step is required:

```powershell
winget install PostgreSQL.PostgreSQL
$psql = "C:\Program Files\PostgreSQL\16\bin\psql.exe"
& $psql -U postgres -c "CREATE ROLE nodepilot WITH LOGIN PASSWORD 'ChangeMe!';"
& $psql -U postgres -c "CREATE DATABASE nodepilot OWNER nodepilot;"
```

Any reachable PostgreSQL works, whether it runs as a service, in a container or as a cluster started with `pg_ctl`.

**2. Run it.** PostgreSQL has to be started first. Should the database not be reachable, the API exits during the migration bootstrap.

```powershell
# Backend on http://localhost:5000
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5432;Database=nodepilot;Username=nodepilot;Password=ChangeMe!;SSL Mode=Disable"
cd src\NodePilot.Api; dotnet run

# Frontend on http://localhost:5173 (proxies /api, /healthz and /hubs to the backend)
cd src\nodepilot-ui; npm install; npm run dev
```

**3. First login.** On an empty database the API writes a one-time setup token to `src\NodePilot.Api\admin-setup.token`. You sign in with the admin username and password you want, after which the login screen asks for the token and creates the Admin account.

## Build & test

```bash
dotnet build                                    # backend
dotnet test                                     # all backend suites (xUnit)
cd src/nodepilot-ui && npm run build            # frontend type-check + build
cd src/nodepilot-ui && npm run test:run         # frontend unit tests (vitest)
cd src/nodepilot-ui && npm run lint:ci          # frontend lint (warning-capped)
cd src/nodepilot-ui && npm run test:e2e         # hermetic Playwright e2e (no backend needed)
cd src/nodepilot-docs-ui && npm run build       # documentation website
cd src/nodepilot-docs-ui && npm run test:run    # docs-site tests incl. the de/en parity guard
```

CI runs the full suites on every pull request. Locally, only the tests that belong to the change are run:

```bash
dotnet test tests/NodePilot.Engine.Tests --filter "FullyQualifiedName~WorkflowCallGraphBuilder"
cd src/nodepilot-ui && npx vitest run src/__tests__/lib/opsTimeline.test.ts
cd src/nodepilot-ui && npx playwright test e2e/operations.spec.ts --config=playwright.dev.config.ts
```

The full suites are reserved for release cuts, dependency bumps and project-wide refactors. Should a change touch something that a guard or parity test watches, such as the activity catalog, API DTOs, migrations, audit codes, trigger keys or the settings schema, that test is run as well. The mapping is listed in [`CLAUDE.md`](CLAUDE.md) under *Build & Test*.

- **Package versions** are centralized in `Directory.Packages.props` under Central Package Management. A package is referenced without a version in the csproj and gets its `<PackageVersion>` entry there.
- **Lint is ratcheted:** `npm run lint:ci` fails on every new warning. Warnings are cleared rather than added.

### Database provider tests

Migrations start at `20260915180058_InitialBaseline`. Databases from the earlier migration history are not upgrade targets, so a new development database is used instead.

Schema and locking changes also have to pass against real PostgreSQL and SQL Server. With Docker running Linux containers:

```powershell
./scripts/Test-DatabaseProviders.ps1
```

The script starts disposable PostgreSQL 16 and SQL Server 2022 containers, runs the `DatabaseIntegration` tests and removes the containers afterwards. Existing servers can be used by setting `NODEPILOT_TEST_POSTGRES` and `NODEPILOT_TEST_SQLSERVER` to administrative connection strings outside tracked files. A provider without a connection string is skipped. Since CI only covers PostgreSQL, the SQL Server part stays a local step.

### Documentation website

`src/nodepilot-docs-ui` contains the docs, which are served at [www.nodepilot.run/docs](https://www.nodepilot.run/docs/) and inside the product at `/docs`, as well as the project website. Build, routing, preview and publishing are described in [its README](src/nodepilot-docs-ui/README.md). For writing docs the following applies:

- **Every page exists in German and English:** `content/de/<path>.md`, `content/en/<path>.md` and a title in both `src/i18n/locales/de.json` and `en.json`. Otherwise `src/lib/content.test.ts` fails.
- **Cross-links carry no language prefix** (`../enterprise/folder-rbac`), because the active language is added at runtime.
- **Only `content/en/` feeds the in-product AI assistant.**
- The docs render their own `content/` corpus and not `docs/`. A change to `docs/` therefore reaches the site only when it is mirrored deliberately.
- `index.html` must not contain an inline `<script>`, because the product serves it under `script-src 'self'`.

## Conventions

- **Tests are mandatory.** Every behavioral change ships with tests in the same PR. Test names follow `MethodName_Scenario_ExpectedResult`. The remote and WinRM layer is always mocked, and DB tests use in-memory SQLite. The coverage gates are enforced in `.github/workflows/ci.yml` for the backend and in `vitest.config.ts` for the frontend.
- **Models and interfaces live in `NodePilot.Core`.**
- **i18n:** Every user-visible string goes through `react-i18next` in both `de` and `en`. The default UI language is German.
- **Guard tests are load-bearing.** Should one fail, the drift is fixed and the guard stays as it is.
- **No backward-compat shims.** Schema changes go through EF migrations, and old code paths are removed instead of being kept behind flags.
- New code matches the style, comment density and idioms of the surrounding code.

## Commit & PR process

1. Branch off `main` and never commit directly to `main`.
2. Keep commits focused and describe the *why* in the message.
3. Open a PR using the template. CI must be green.
4. Architectural decisions of lasting consequence get an ADR under `docs/adr/`. When one is warranted and the template are described in [`docs/adr/README.md`](docs/adr/README.md).

## Where to look

- `README.md`: feature overview, configuration reference and project layout.
- `CONTEXT.md`: domain glossary.
- `docs/`: subsystem deep-dives.
- `docs/adr/`: architecture decision records.
