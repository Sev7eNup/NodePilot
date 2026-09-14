# NodePilot Load Tests

End-to-end load-test harness built on NBomber. Hammers the real REST API, observes SignalR,
and exercises the engine / DB / hub path with big, complex workflows.

## One-time setup

Use a dedicated local database and Windows PowerShell 5.1. Run the preparation commands from
the repository root. The API runs in **Development**, bound to loopback; these settings are for
an isolated load-test machine. Noop reports synthetic script success and requires explicit
acknowledgement. Do not reuse these settings for a production deployment.

1. **Start the support stack** (SQL Server + Prometheus + Grafana):

   ```powershell
   Push-Location tests/NodePilot.LoadTests
   Copy-Item .env.example .env   # first setup only; set both passwords before the next command
   docker compose up -d
   Pop-Location
   ```

   `.env` must define `MSSQL_SA_PASSWORD` and `GF_SECURITY_ADMIN_PASSWORD`; Compose rejects
   missing values. SQL Server listens at `localhost:1433`, Prometheus at `localhost:9090`,
   and Grafana at `localhost:3000` (user `admin`, password from `.env`). Anonymous Grafana
   access is off by default. Keep the support ports confined to the isolated test machine.
   Wait until SQL Server is ready before starting the API. With an existing SQL data volume,
   use its current SA password: changing `.env` does not reset an initialized database password.

2. **Configure the API in this PowerShell session.** Read the resolved Compose password instead
   of copying a second password into an appsettings file. The connection-string builder also
   quotes passwords containing connection-string delimiters correctly.

   ```powershell
   $loadStack = docker compose -f tests/NodePilot.LoadTests/docker-compose.yml --env-file tests/NodePilot.LoadTests/.env config --format json | ConvertFrom-Json
   $loadDb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
   $loadDb['Data Source'] = 'localhost,1433'
   $loadDb['Initial Catalog'] = 'NodePilotLoadTest'
   $loadDb['User ID'] = 'sa'
   $loadDb['Password'] = $loadStack.services.sqlserver.environment.MSSQL_SA_PASSWORD
   $loadDb['Encrypt'] = $true
   $loadDb['TrustServerCertificate'] = $true

   $env:ASPNETCORE_ENVIRONMENT = 'Development'
   $env:DOTNET_ENVIRONMENT = 'Development'
   $env:Database__Provider = 'sqlserver'
   $env:Database__AllowInsecureTls = 'true'
   $env:ConnectionStrings__DefaultConnection = $loadDb.ConnectionString
   $env:Remote__Provider = 'noop'
   $env:Remote__AllowNoop = 'true'
   $env:Remote__Noop__MinLatencyMs = '5'
   $env:Remote__Noop__MaxLatencyMs = '20'
   $env:Logging__StepDetail__Enabled = 'false'
   $env:OpenTelemetry__Enabled = 'true'
   $env:OpenTelemetry__Exporters__PrometheusScrape = 'true'
   $env:OpenTelemetry__Exporters__PrometheusScrapeAllowAnonymous = 'true'
   ```

   `Remote:AllowNoop=true` (alternatively `NODEPILOT_ALLOW_NOOP_REMOTE=1`) is mandatory;
   `Remote:Provider=noop` alone aborts startup. `Database:AllowInsecureTls=true` is allowed
   here because the database is loopback-only and the environment is Development. A custom
   environment named `Loadtest` does **not** qualify. Server deployments outside Development
   require verified TLS (`Encrypt=Strict;TrustServerCertificate=False` for SQL Server).
   No tracked appsettings file needs to change.

3. **Start the API in the same terminal:**

   ```powershell
   Push-Location src/NodePilot.Api
   dotnet run --no-launch-profile -- --urls http://localhost:5000
   Pop-Location
   ```

   The terminal remains occupied until the API stops. Closing it afterward discards the
   session-only overrides. Confirm `/healthz/ready` is healthy before running scenarios.
   The bundled Prometheus targets `host.docker.internal:5000`; verify that Docker Desktop
   can reach the host listener. If it cannot, run Prometheus on the host with a
   `localhost:5000` scrape target while keeping the API bound to loopback.

4. **Create the first load-test admin** in a second PowerShell terminal, from the repository
   root. On a fresh database, login requires the one-shot `X-Setup-Token` header. With the
   default token location, the API writes `admin-setup.token` into its content root:

   ```powershell
   $loadSetupToken = (Get-Content -LiteralPath src/NodePilot.Api/admin-setup.token -Raw).Trim()
   $loadLogin = @{ username = 'loadtest'; password = 'loadtest-password' } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/auth/login -ContentType 'application/json' -Headers @{ 'X-Setup-Token' = $loadSetupToken } -Body $loadLogin
   Remove-Variable loadSetupToken, loadLogin
   ```

   Without a valid token the first login returns `401 SETUP_TOKEN_REQUIRED`. Bootstrap marks
   the first admin as a break-glass account, so the default `BreakGlassOnly` login policy
   permits subsequent harness logins. If the database already has users, use an existing
   authorized test admin instead; bootstrap cannot create another one. The example credentials
   match `appsettings.loadtests.json` and are only for this disposable local setup. If changed,
   set `LOADTEST__Username` and `LOADTEST__Password` in the terminal running the harness. The
   harness requests a bearer token itself; it does not consume a browser cookie jar.

## Running scenarios

```bash
cd tests/NodePilot.LoadTests

# Quick smoke
dotnet run -c Release -- --scenario soak --rps 5 --duration 60

# Soak — default 10 RPS × 30 min
dotnet run -c Release -- --scenario soak

# Spike — 0 → 200 RPS → 0
dotnet run -c Release -- --scenario spike

# Ramp — linear 1 → 500 RPS over 10 min; look for the knee-point
dotnet run -c Release -- --scenario ramp

# Burst — 100 executions in <1 s, wait for drain
dotnet run -c Release -- --scenario burst --concurrency 100
```

Reports land under `reports/` as `loadtest-<scenario>-<timestamp>.{html,txt,md}`.

## Workflow templates

`WorkflowTemplates.cs` builds four families; `Seeder` creates `CopiesPerTemplate` of each
per run with unique suffixes so concurrent runs don't clash.

| Template | Shape | Targets |
|---|---|---|
| Deep sequential | 50 runScript steps in a chain | step scheduling overhead, DB write rate |
| Wide fan-out | 1 → 30 parallel scripts → junction(waitAll) | engine parallelism, per-step DI scope |
| Mixed heavy | 5-way fan-out with runScript / restApi / log / delay + junction + 5-way post-fan + returnData | realistic profile, all paths |
| Sub-workflow nest | Parent → Child → Grandchild (3 levels, near MaxCallDepth=10) | startWorkflow scope isolation |

## Pass / fail gates

The runner exits non-zero if:

- Success rate < 99.5 %
- Any executions are still in `Running` after the scenario ends (indicates leaking)

Latency and ThreadPool gates are currently visual (Grafana) — promote to hard gates once
a baseline is established.

## Observability during runs

- Grafana dashboard (auto-imported): http://localhost:3000/dashboards — "NodePilot Load Test"
- Prometheus: http://localhost:9090 — query raw metrics
- NBomber HTML report: `reports/loadtest-<scenario>-*.html`
- Optional SignalR latency observer: set `"SignalR": { "Enabled": true, "SampleRate": 0.1 }`
  in `appsettings.loadtests.json`. Prints broadcast p50/95/99 at end of run.

## Troubleshooting

- **Seeding fails with 401/403**: the first login bootstraps the Admin user. Make sure you
  started against a fresh DB (or a DB where the `loadtest` user is an Admin).
- **"Machine 'loadtest-target' not registered, using ad-hoc hostname"** in API logs: expected.
  The NoOp session factory handles it regardless of whether the machine is registered.
- **Burst scenario hangs**: the scenario intentionally waits `TerminalTimeoutSeconds` for all
  triggered executions to drain. Tune the value if your workflows are larger.
