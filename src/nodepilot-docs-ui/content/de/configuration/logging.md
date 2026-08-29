# Logging

NodePilot schreibt Anwendungslogs über Serilog. `Logging:Format` bestimmt das Ausgabeformat:

| Wert | Format | Use Case |
|---|---|---|
| `text` (default) | Plain-Text | Dev, Console |
| `cmtrace` | CMTrace-kompatibel | Ops auf Windows, CMTrace.exe |
| `json` | JSON (CLEF) | Generic structured ingest |
| `ecs-json` | ECS 1.x | SIEM — siehe [SIEM-Logging](../enterprise/siem-logging) |

## Wo die Dateien liegen

Zwei Datei-Sinks, beide im selben Ordner:

| Sink | Muster | Schlüssel | Ohne Schlüssel |
|---|---|---|---|
| Anwendungslog | `nodepilot-JJJJMMTT.log` | `Logging:File:Path` | `{ContentRoot}\logs\nodepilot-.log` |
| Support-Log | `nodepilot-support-JJJJMMTT.log` | `Logging:SupportLog:Path` | `{ContentRoot}\logs\nodepilot-support-.log` |

Ein relativer Wert wird gegen den ContentRoot aufgelöst, ein absoluter unverändert übernommen. Die Installationsprogramme setzen beide Schlüssel auf `C:\ProgramData\NodePilot\logs\`; eine aus dem Quellcode gestartete Entwicklungsinstanz schreibt entsprechend nach `src\NodePilot.Api\logs\`.

Das Anwendungslog rollt täglich und zusätzlich bei `Logging:File:FileSizeLimitBytes` (Default 100 MB, Suffix `_001`, `_002` …). `Logging:File:RetainedFileCountLimit` (Default **7**) begrenzt die **Anzahl** aufbewahrter Dateien: auf einem gesprächigen System sind sieben Dateien weniger als sieben Tage.

Ausgeliefert wird `Logging:Format=cmtrace`, nicht der Code-Default `text`. Die Sektion ist nicht hot-reloadbar — ein Formatwechsel wirkt erst nach einem Neustart des Dienstes.

Welche Datei bei welchem Störungsbild weiterhilft, steht unter [Logs & Diagnose](../deployment/logs).

## Output-Redaction

`OutputRedactor` maskiert Secrets. **Immer aktiv.** Custom-Patterns via `Logging:Redaction:Patterns`.

## Support-Log & SupportEvents

Zwei Sub-Sinks aus demselben Serilog-Filter (für Operator/Ticket-Diagnose):

1. **Plain-Text-File** `{Logging:SupportLog:Path}` (Produktion: `C:\ProgramData\NodePilot\logs\nodepilot-support-*.log`). `Logging:SupportLog:RetainedFileCountLimit` (default `90`) begrenzt die **Anzahl** aufbewahrter Dateien, nicht die Tage: bei täglichem Rollover entspricht das ~90 Tagen, aber `Logging:SupportLog:FileSizeLimitBytes` (default 10 MiB) rollt zusätzlich innerhalb eines Tages — auf sehr gesprächigen Systemen deckt das Limit entsprechend weniger Tage ab.
2. **DB-Tabelle `SupportEvents`** für den Web-Viewer (Filter/Cursor/Export) — Toggle `Logging:SupportLog:DbProjectionEnabled` (default `true`). Geschrieben via gepuffertem `SupportEventFlushService`, getrimmt durch `SupportEventRetentionService` (90 d).

Endpoints: `GET /api/diagnostics/support-log|support-log/download|support-events|support-events/export` (Admin). UI: eigene Hauptmenü-Seite `/support-log` (Admin-only, im Sidebar unter „Alerting").

## Security-Headers (Non-Dev)

HSTS, CSP, `X-Frame-Options=DENY`, `nosniff`, `Referrer-Policy`.

## SignalR-Auth

httpOnly `np_auth`-Cookie beim WebSocket-Upgrade (nur `/hubs/`); kein `?access_token=`-Querystring.
