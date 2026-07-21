# Observability

## OpenTelemetry (opt-in)

OpenTelemetry ist opt-in. Setup in `NodePilot.Telemetry` — Constants, Options, `PrometheusClient`.

## Prometheus-Scrape

`OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` (default `false`) — `/metrics` ist **nicht** anonym erreichbar. Auf `true` setzen, wenn der Scraper ohne Auth zugreifen soll (Relaxation, bewusst setzen).

## Observability-API

| Endpoint | Zweck |
|---|---|
| `GET /api/observability/config` | Aktuelle Observability-Config |
| `GET /api/observability/query` | PromQL-Query |
| `GET /api/observability/query_range` | PromQL-Range-Query |
| `GET /api/observability/summary` | Zusammenfassung |

CLI: `np observability summary|query|query-range`.

## SIEM-Logging

Für strukturierte Log-Ingestion ins SIEM (Elastic/Sentinel/Splunk) `Logging:Format=ecs-json` setzen — siehe [SIEM-Logging](../enterprise/siem-logging).

## Support-Diagnostics

Für Operator/Ticket-Diagnose: `GET /api/diagnostics/support-log|support-log/download|support-events|support-events/export` (Admin). Siehe [Logging](../configuration/logging).

## Metrics (Auszug)

- `nodepilot.audit_archive.hash_drift` — Audit-Archive-Drift.
- `nodepilot_credential_crypto_calls{operation,result}` — `encrypt`/`decrypt` × `success`/`failure`.
- `nodepilot_credential_crypto_legacy_reads` — Decrypts aus Legacy-Provider (Migration-Window).