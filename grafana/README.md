# NodePilot · Grafana Stack

A self-contained Prometheus + Grafana stack with **10 pre-provisioned dashboards** covering every layer NodePilot exposes telemetry for.

```
┌────────────┐   scrape /metrics   ┌────────────┐   query    ┌─────────┐
│ NodePilot  │ ──────────────────▶ │ Prometheus │ ─────────▶ │ Grafana │
│   API      │   (every 15 s)      │  :9090     │            │  :3000  │
└────────────┘                     └────────────┘            └─────────┘
```

## Quickstart

1. **Start the NodePilot API with the Prometheus exporter on**:

   ```powershell
   $env:OpenTelemetry__Enabled = "true"
   $env:OpenTelemetry__Exporters__PrometheusScrape = "true"
   $env:OpenTelemetry__Exporters__PrometheusScrapeAllowAnonymous = "true"
   dotnet run --project src/NodePilot.Api --urls "http://localhost:5000"
   ```

   Equivalent in `appsettings.Development.json`:
   ```json
   {
     "OpenTelemetry": {
       "Enabled": true,
       "Exporters": {
         "PrometheusScrape": true,
         "PrometheusScrapeAllowAnonymous": true
       }
     }
   }
   ```

2. **Bring up the monitoring stack**:

   ```bash
   cd grafana
   cp .env.example .env
   # Generate a unique value for NODEPILOT_GRAFANA_ADMIN_PASSWORD before continuing.
   # PowerShell 5.1 example:
   # $r = [Security.Cryptography.RandomNumberGenerator]::Create()
   # $b = New-Object byte[] 32
   # try { $r.GetBytes($b); [Convert]::ToBase64String($b) } finally { $r.Dispose(); [Array]::Clear($b, 0, $b.Length) }
   docker compose up -d
   ```

3. **Open the dashboards**:

   - <http://localhost:3000> (admin / your password)
   - All 10 dashboards live in the **NodePilot** folder
   - Or jump directly: <http://localhost:3000/d/nodepilot-mission-control>

> **Tip**: append `?kiosk=tv` to any dashboard URL for a full-bleed wallboard view, perfect for an ops monitor.

## Network and authentication boundary

The shipped Compose file binds Grafana and Prometheus to `127.0.0.1` only. Prometheus has no
application login in this stack; Grafana requires its unique administrator password, disables
anonymous access, and disables self-service sign-up. Do not publish ports 3000 or 9090 directly
and do not replace the loopback host bindings with `0.0.0.0`.

For remote operator access, place a host-based, authenticated TLS reverse proxy (for example IIS,
nginx, or Caddy) in front of the loopback endpoints. The proxy must terminate HTTPS with a trusted
certificate and require organization authentication for **both** routes; Grafana's own login remains
enabled as defense in depth, while Prometheus depends entirely on the proxy authentication boundary.
Restrict proxy ingress to the administration network and configure Grafana's public root URL to the
HTTPS origin. The Compose stack itself is intentionally not an Internet-facing TLS endpoint.

The `.env` file is ignored by Git. Restrict its filesystem permissions to the operator/service
account because it contains the Grafana administrator credential. Existing deployments must replace
the legacy `GF_SECURITY_ADMIN_PASSWORD` entry with a freshly generated
`NODEPILOT_GRAFANA_ADMIN_PASSWORD`; the old name is intentionally ignored so previously documented
defaults cannot survive an upgrade. Never start the stack with the example's blank value.

### Rotate the password when upgrading an existing stack

Grafana applies `GF_SECURITY_ADMIN_PASSWORD` only on the first start of a new database. Changing
`.env` therefore does **not** rotate the administrator credential stored in an existing persistent
`grafana-data` volume. Before recreating an existing stack, reset that stored credential explicitly:

```powershell
$grafanaRng = [Security.Cryptography.RandomNumberGenerator]::Create()
$grafanaSecretBytes = New-Object byte[] 32
try {
  $grafanaRng.GetBytes($grafanaSecretBytes)
  $newGrafanaAdminPassword = [Convert]::ToBase64String($grafanaSecretBytes)
}
finally {
  $grafanaRng.Dispose()
  [Array]::Clear($grafanaSecretBytes, 0, $grafanaSecretBytes.Length)
}
$env:NODEPILOT_GRAFANA_ADMIN_PASSWORD = $newGrafanaAdminPassword

$newGrafanaAdminPassword |
  docker compose exec -T grafana grafana cli --homepath /usr/share/grafana `
    admin reset-admin-password --password-from-stdin
if ($LASTEXITCODE -ne 0) { throw "Grafana password rotation failed" }

$remainingLines = Get-Content -LiteralPath .env |
  Where-Object { $_ -notmatch '^(GF_SECURITY_ADMIN_PASSWORD|NODEPILOT_GRAFANA_ADMIN_PASSWORD)=' }
$remainingLines += "NODEPILOT_GRAFANA_ADMIN_PASSWORD=$newGrafanaAdminPassword"
[IO.File]::WriteAllLines(
  (Join-Path $PWD '.env'), $remainingLines, [Text.UTF8Encoding]::new($false))

Remove-Item Env:NODEPILOT_GRAFANA_ADMIN_PASSWORD
Remove-Variable newGrafanaAdminPassword
Remove-Variable grafanaSecretBytes
Remove-Variable grafanaRng
docker compose up -d
```

The CLI reads the replacement from standard input, so it is not exposed in the process command
line. If the old stack is no longer running, start the existing Grafana container without recreating
it, perform the same reset, and only then run `docker compose up -d`. As an alternative, discard the
volume only after exporting or intentionally abandoning all locally stored dashboards, users, and
settings. See Grafana's [configuration](https://grafana.com/docs/grafana/latest/setup-grafana/configure-grafana/)
and [administrator CLI](https://grafana.com/docs/grafana/latest/administration/cli/) documentation.

## Immutable image updates

Both images are pinned by a readable upstream version **and** the corresponding multi-architecture
manifest digest. `docker compose pull` therefore retrieves only the reviewed immutable image. A
security update is explicit: review the upstream release notes, select the new version, obtain its
manifest-list digest from the verified publisher (`docker buildx imagetools inspect <image>:<tag>`),
update both parts of the image reference, then run `docker compose pull` and restart the stack. Never
replace these references with `latest` or a version tag without a digest.

## Dashboard Catalogue

The stack provisions 10 dashboards. They share a consistent visual grammar:

- **RED method** at the top (Rate · Errors · Duration) for service-level views
- **USE method** (Utilization · Saturation · Errors) for the .NET runtime view
- Coloured stat cards (green/blue → ok, amber → warning, red → critical)
- Smoothed time-series with semi-transparent fills, no point markers
- Per-row narrative: KPIs → trends → top-N rankings → drill-down tables
- Cross-dashboard nav header, plus header pills automatically populated by Grafana

| # | UID | Title | Audience | Default range |
|---|---|---|---|---|
| 00 | `nodepilot-mission-control` | **Mission Control** — single-pane status of throughput, errors, latency, top offenders, system saturation | On-call, leadership | 1 h |
| 10 | `nodepilot-workflows-v2` | **Workflows** — per-workflow KPIs, status timeline, latency percentiles, graph depth, sub-workflow behaviour | SRE, workflow owners | 24 h |
| 20 | `nodepilot-activities` | **Activities** — step-level performance by `activity_type`, retry attempts, backoff cost, output redaction, debug sessions | Engine maintainers | 1 h |
| 30 | `nodepilot-winrm` | **WinRM** — session lifecycle, script latency, auth failures, DPAPI credential pipeline | Remote-execution oncall | 1 h |
| 40 | `nodepilot-triggers` | **Triggers & Scheduler** — orchestrator sync, trigger fires, webhook ingress, retention sweepers | Workflow operators | 1 h |
| 50 | `nodepilot-api` | **API & HTTP** — RED method per route, latency percentiles, status-class breakdown, SignalR & Kestrel | API team | 1 h |
| 60 | `nodepilot-runtime` | **Runtime** — CPU, memory, GC, ThreadPool, JIT, locks, exceptions (USE method) | Performance engineers | 1 h |
| 70 | `nodepilot-security` | **Security & Audit** — login health, audit pipeline, sensitive CRUD, rate limits, redaction hits | Security team | 6 h |
| 80 | `nodepilot-ai` | **AI / LLM** — call volume, token usage, error taxonomy, model latency, plus import/export & machine-test panels | AI feature owners | 6 h |
| 90 | `nodepilot-database` | **Database** — EF SaveChanges hot path, row counts, failures, dispatch loop | DB engineers | 1 h |

## What Mission Control Tells You In 5 Seconds

The landing dashboard is structured around the questions an operator asks:

1. **Are we serving traffic?** — `Active Executions`, `Throughput`, `HTTP RPS`
2. **Is anything failing?** — `Success Rate`, `Failed (1h)`, `5xx Rate`
3. **How fast?** — `p95 Duration`, latency percentile chart
4. **What broke?** — top-10 by failures, top-10 slowest, per-workflow scorecard
5. **Are we close to limits?** — saturation row at the bottom (CPU / memory / GC / threadpool / RPS / 5xx)

Each scorecard row is clickable: click the workflow name to open the **Workflows** drilldown, prefiltered to that workflow.

## Adding A New Dashboard

1. Drop a new JSON file into `grafana/grafana/dashboards/` (any filename — the provisioner picks them up by glob).
2. Use the same shape as an existing one:
   - `"datasource": { "type": "prometheus", "uid": "nodepilot-prometheus" }` everywhere
   - Header text panel + cross-link bar (copy from `00-mission-control.json`)
   - `schemaVersion: 39`, `refresh: "30s"`, `tags: ["nodepilot", ...]`
3. Provisioner reloads every 10 s — no Grafana restart needed.
4. Run `python3 grafana/fix-legend-formats.py` to ensure every stat target has a clean legend (idempotent — it's a guard, not an editor).

## Metric Naming Reference

OpenTelemetry's Prometheus exporter rewrites instrument names:
- `nodepilot.executions.started` (Counter, unit `1`) → `nodepilot_executions_started_total`
- `nodepilot.execution.duration` (Histogram, unit `ms`) → `nodepilot_execution_duration_milliseconds_{bucket,sum,count}`
- `nodepilot.executions.active` (UpDownCounter, unit `1`) → `nodepilot_executions_active` (no `_total`)

The full validated inventory — every metric, every label key, every unit — is at [`METRICS_INVENTORY.md`](./METRICS_INVENTORY.md). When you add a new instrument, update that file too.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Grafana → "datasource not found" on every panel | DB volume holds stale provisioning state | `docker compose down && docker volume rm grafana_grafana-data && docker compose up -d` |
| Mission Control panels show "No data" | NodePilot exporter off | Check `OpenTelemetry:Enabled=true` and curl `http://localhost:5000/metrics` directly |
| Prometheus target down | host.docker.internal not resolving on Linux | Set `NODEPILOT_HOST=<host-ip>` in `.env` and reference it in `prometheus/prometheus.yml` |
| `nodepilot_*_milliseconds_bucket` not found | Old dashboard pre-OTel-1.10 used `_bucket` directly | All panels in this repo use the `_milliseconds_bucket` form already |
| Stat card shows `__name__{label1=...}` | Target without `legendFormat` | Run `python3 grafana/fix-legend-formats.py` |

## File Layout

```
grafana/
├── docker-compose.yml          # prom + grafana, host.docker.internal wired up
├── .env.example                # NODEPILOT_GRAFANA_ADMIN_PASSWORD template
├── README.md                   # this file
├── METRICS_INVENTORY.md        # validated metric catalogue
├── RAW_METRICS_SAMPLE.txt      # snapshot of /metrics for quick lookup
├── fix-legend-formats.py       # one-shot lint to ensure clean stat legends
├── prometheus/
│   └── prometheus.yml          # scrape config (15 s interval)
└── grafana/
    ├── provisioning/
    │   ├── datasources/prometheus.yml
    │   └── dashboards/dashboards.yml
    └── dashboards/
        ├── 00-mission-control.json
        ├── 10-workflows.json
        ├── 20-activities.json
        ├── 30-winrm.json
        ├── 40-triggers.json
        ├── 50-api.json
        ├── 60-runtime.json
        ├── 70-security.json
        ├── 80-ai.json
        └── 90-database.json
```
