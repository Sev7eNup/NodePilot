# NodePilot Load Tests

End-to-end load-test harness built on NBomber. Hammers the real REST API, observes SignalR,
and exercises the engine / DB / hub path with big, complex workflows.

## One-time setup

1. **Start support stack** (SQL Server + Prometheus + Grafana):

   ```bash
   cd tests/NodePilot.LoadTests
   docker compose up -d
   ```

   - SQL Server: `localhost:1433`, SA password `LoadTest!Password1`
   - Prometheus: http://localhost:9090
   - Grafana: http://localhost:3000 (anonymous Viewer, admin/admin to edit)

2. **Configure the API host for load testing.** Create `src/NodePilot.Api/appsettings.Loadtest.json`:

   ```json
   {
     "Database": {
       "Provider": "sqlserver"
     },
     "ConnectionStrings": {
       "DefaultConnection": "Server=localhost,1433;Database=NodePilotLoadTest;User Id=sa;Password=LoadTest!Password1;TrustServerCertificate=True"
     },
     "Remote": {
       "Provider": "noop",
       "Noop": { "MinLatencyMs": 5, "MaxLatencyMs": 20 }
     },
     "Logging": {
       "StepDetail": { "Enabled": false }
     },
     "OpenTelemetry": {
       "Enabled": true,
       "Exporters": { "PrometheusScrape": true }
     }
   }
   ```

3. **Start the API against the Loadtest profile:**

   ```bash
   cd src/NodePilot.Api
   dotnet run --environment Loadtest --urls http://localhost:5000
   ```

4. **Create the load-test user** via a normal login against the fresh DB — the first login
   auto-creates an Admin with the credentials you provide. Use the same credentials as
   `appsettings.loadtests.json` (`loadtest` / `loadtest-password`).

   ```bash
   curl -X POST http://localhost:5000/api/auth/login \
        -H "Content-Type: application/json" \
        -d '{"username":"loadtest","password":"loadtest-password"}'
   ```

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
