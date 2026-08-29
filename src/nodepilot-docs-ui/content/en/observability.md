# Observability

NodePilot provides metrics, traces and an observability API. OpenTelemetry is disabled by default and has to be configured explicitly.

## OpenTelemetry (opt-in)

OpenTelemetry is opt-in. The setup is in `NodePilot.Telemetry` — constants, options, `PrometheusClient`.

## Prometheus scraping

`OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` (default `false`) — `/metrics` is **not** reachable anonymously. Set it to `true` if the scraper should access it without authentication (a relaxation; set it deliberately).

## Host-name redaction

`OpenTelemetry:RedactHostnames` (default `true`) keeps the host name out of the telemetry: `service.instance.id` is a process-stable random ID instead of `hostname:pid`, the resource attribute `host.name` is omitted, and the Serilog bridge suppresses the field. Anyone who needs host attribution in Tempo/Grafana sets the value to `false` deliberately — otherwise the attribution disappears after an upgrade without further notice.

## The observability API

| Endpoint | Purpose |
|---|---|
| `GET /api/observability/config` | The current observability configuration |
| `GET /api/observability/query` | PromQL query |
| `GET /api/observability/query_range` | PromQL range query |
| `GET /api/observability/summary` | Summary |

CLI: `np observability summary|query|query-range`.

## SIEM logging

For structured log ingest into Elastic, Sentinel or Splunk, set `Logging:Format=ecs-json`. Details: [SIEM logging](enterprise/siem-logging).

## Support diagnostics

For operations and ticket diagnosis, `GET /api/diagnostics/support-log|support-log/download|support-events|support-events/export` are available to admins. Details: [Logging](configuration/logging). Which log files exist, where they live and which one helps with which failure mode: [Logs & diagnostics](deployment/logs).

## Metrics (excerpt)

- `nodepilot.database.requests_rejected` — requests the open database breaker terminated immediately with 503.
- `nodepilot.database.outages` — confirmed outage episodes.
- `nodepilot.database.probe_cleanup_timeouts` — aborted cleanup steps of the recovery probe.
- `nodepilot.scheduler.triggers.dropped_db_unavailable` — trigger fires observed and discarded during a known outage.
- `nodepilot.audit_archive.hash_drift` — audit archive drift.
- `nodepilot_credential_crypto_calls{operation,result}` — `encrypt`/`decrypt` × `success`/`failure`.
- `nodepilot_credential_crypto_legacy_reads` — decrypts from the legacy provider (migration window).

For monitoring: `/healthz/ready` is the traffic gate and returns 503 on `Armed` or `Unavailable`.
`/healthz/database` always answers 200 and reports `ok`, `armed` or `unavailable`, including a rough
cause. Each recovery produces exactly one `DATABASE_RECOVERED` audit entry; a trip audit cannot be
written reliably during an outage.
