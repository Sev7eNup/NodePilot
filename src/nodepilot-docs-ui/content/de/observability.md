# Observability

NodePilot stellt Metriken, Traces und eine Observability-API bereit. OpenTelemetry ist standardmäßig deaktiviert und muss explizit konfiguriert werden.

## OpenTelemetry (opt-in)

OpenTelemetry ist opt-in. Setup in `NodePilot.Telemetry` — Constants, Options, `PrometheusClient`.

## Prometheus-Scrape

`OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` (default `false`) — `/metrics` ist **nicht** anonym erreichbar. Auf `true` setzen, wenn der Scraper ohne Auth zugreifen soll (Relaxation, bewusst setzen).

## Hostname-Redaktion

`OpenTelemetry:RedactHostnames` (default `true`) hält den Hostnamen aus der Telemetrie: `service.instance.id` ist eine prozessstabile Zufalls-Id statt `hostname:pid`, das Resource-Attribut `host.name` entfällt, und die Serilog-Bridge unterdrückt das Feld. Wer Host-Attribution in Tempo/Grafana braucht, setzt den Wert bewusst auf `false` — nach einem Upgrade verschwindet die Zuordnung sonst ohne weiteren Hinweis.

## Observability-API

| Endpoint | Zweck |
|---|---|
| `GET /api/observability/config` | Aktuelle Observability-Config |
| `GET /api/observability/query` | PromQL-Query |
| `GET /api/observability/query_range` | PromQL-Range-Query |
| `GET /api/observability/summary` | Zusammenfassung |

CLI: `np observability summary|query|query-range`.

## SIEM-Logging

Für strukturierte Log-Aufnahme in Elastic, Sentinel oder Splunk ist `Logging:Format=ecs-json` zu setzen. Details: [SIEM-Logging](enterprise/siem-logging).

## Support-Diagnostics

Für Betriebs- und Ticketdiagnose stehen `GET /api/diagnostics/support-log|support-log/download|support-events|support-events/export` für Admins bereit. Details: [Logging](configuration/logging). Welche Logdateien es gibt, wo sie liegen und welche bei welchem Störungsbild weiterhilft: [Logs & Diagnose](deployment/logs).

## Metrics (Auszug)

- `nodepilot.database.requests_rejected` — Requests, die der offene Datenbank-Breaker sofort mit 503 beendet.
- `nodepilot.database.outages` — bestätigte Ausfall-Episoden.
- `nodepilot.database.probe_cleanup_timeouts` — abgebrochene Cleanup-Schritte der Recovery-Sonde.
- `nodepilot.scheduler.triggers.dropped_db_unavailable` — während eines bekannten Ausfalls beobachtete und verworfene Trigger-Fires.
- `nodepilot.audit_archive.hash_drift` — Audit-Archive-Drift.
- `nodepilot_credential_crypto_calls{operation,result}` — `encrypt`/`decrypt` × `success`/`failure`.
- `nodepilot_credential_crypto_legacy_reads` — Decrypts aus Legacy-Provider (Migration-Window).

Für Monitoring gilt: `/healthz/ready` ist das Traffic-Gate und liefert bei `Armed` oder `Unavailable`
503. `/healthz/database` antwortet immer 200 und berichtet `ok`, `armed` oder `unavailable` inklusive
grober Ursache. Pro Erholung entsteht genau ein Audit-Eintrag `DATABASE_RECOVERED`; ein Trip-Audit ist
während des Ausfalls nicht zuverlässig schreibbar.
