# Logging

Serilog. Format via `Logging:Format`:

| Wert | Format | Use Case |
|---|---|---|
| `text` (default) | Plain-Text | Dev, Console |
| `cmtrace` | CMTrace-kompatibel | Ops auf Windows, CMTrace.exe |
| `json` | JSON (CLEF) | Generic structured ingest |
| `ecs-json` | ECS 1.x | SIEM — siehe [SIEM-Logging](../enterprise/siem-logging) |

## Output-Redaction

`OutputRedactor` maskiert Secrets. **Immer aktiv.** Custom-Patterns via `Logging:Redaction:Patterns`.

## Support-Log & SupportEvents

Zwei Sub-Sinks aus demselben Serilog-Filter (für Operator/Ticket-Diagnose):

1. **Plain-Text-File** `{Logging:SupportLog:Path}` (Produktion: `C:\ProgramData\NodePilot\logs\nodepilot-support-*.log`), `RetainedFileCountLimit` 90 Tage.
2. **DB-Tabelle `SupportEvents`** für den Web-Viewer (Filter/Cursor/Export) — Toggle `Logging:SupportLog:DbProjectionEnabled` (default `true`). Geschrieben via gepuffertem `SupportEventFlushService`, getrimmt durch `SupportEventRetentionService` (90 d).

Endpoints: `GET /api/diagnostics/support-log|support-log/download|support-events|support-events/export` (Admin). UI: eigene Hauptmenü-Seite `/support-log` (Admin-only, im Sidebar unter „Alerting").

## Security-Headers (Non-Dev)

HSTS, CSP, `X-Frame-Options=DENY`, `nosniff`, `Referrer-Policy`.

## SignalR-Auth

httpOnly `np_auth`-Cookie beim WebSocket-Upgrade (nur `/hubs/`); kein `?access_token=`-Querystring.