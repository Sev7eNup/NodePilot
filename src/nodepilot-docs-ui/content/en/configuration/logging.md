# Logging

NodePilot writes application logs through Serilog. `Logging:Format` determines the output format:

| Value | Format | Use case |
|---|---|---|
| `text` (default) | Plain text | Development, console |
| `cmtrace` | CMTrace-compatible | Operations on Windows, CMTrace.exe |
| `json` | JSON (CLEF) | Generic structured ingest |
| `ecs-json` | ECS 1.x | SIEM — see [SIEM logging](../enterprise/siem-logging) |

## Where the files live

Two file sinks, both in the same folder:

| Sink | Pattern | Key | Without the key |
|---|---|---|---|
| Application log | `nodepilot-YYYYMMDD.log` | `Logging:File:Path` | `{ContentRoot}\logs\nodepilot-.log` |
| Support log | `nodepilot-support-YYYYMMDD.log` | `Logging:SupportLog:Path` | `{ContentRoot}\logs\nodepilot-support-.log` |

A relative value is resolved against the content root, an absolute one is taken as it is. The installers set both keys to `C:\ProgramData\NodePilot\logs\`; a development instance started from source writes to `src\NodePilot.Api\logs\` accordingly.

The application log rolls daily and additionally at `Logging:File:FileSizeLimitBytes` (100 MB by default, suffix `_001`, `_002` …). `Logging:File:RetainedFileCountLimit` (**7** by default) limits the **number** of retained files: on a talkative system seven files are fewer than seven days.

What ships is `Logging:Format=cmtrace`, not the code default `text`. The section is not hot-reloadable — a format change takes effect only after a service restart.

Which file helps with which failure mode is covered under [Logs & diagnostics](../deployment/logs).

## Output redaction

`OutputRedactor` masks secrets. **Always active.** Custom patterns via `Logging:Redaction:Patterns`; a pattern that is not a valid regular expression stops the service at startup instead of being skipped.

## Support log & SupportEvents

Two sub-sinks from the same Serilog filter (for operator/ticket diagnosis):

1. **A plain-text file** at `{Logging:SupportLog:Path}` (production: `C:\ProgramData\NodePilot\logs\nodepilot-support-*.log`). `Logging:SupportLog:RetainedFileCountLimit` (default `90`) limits the **number** of retained files, not the number of days: with daily rollover that corresponds to roughly 90 days, but `Logging:SupportLog:FileSizeLimitBytes` (default 10 MiB) additionally rolls within a day — on very talkative systems the limit therefore covers correspondingly fewer days.
2. **The database table `SupportEvents`** for the web viewer (filter/cursor/export) — toggled by `Logging:SupportLog:DbProjectionEnabled` (default `true`). Written through the buffered `SupportEventFlushService`, trimmed by `SupportEventRetentionService` (90 d).

Endpoints: `GET /api/diagnostics/support-log|support-log/download|support-events|support-events/export` (admin). UI: its own main-menu page `/support-log` (admin only, in the sidebar under "Alerting").

## Server log (application log) in the UI

The same page shows the application log `nodepilot-YYYYMMDD.log` through the **Server log** switch (`/support-log?source=server`): errors, warnings and start-up messages from NodePilot itself. **Problems** folds repeated causes into one card with a count; **History** lists every entry with filters for severity, area and text. This works while the API answers; start-up failures of earlier starts show up once the service is back. When the API does not answer, file access remains necessary (see [Logs & diagnostics](../deployment/logs)).

The server reads the files newest-first within an 8 MiB budget and reports in every response how much of the window it saw. **"No problems" appears only when the whole window was read**; otherwise the page says "in the part that was read (since …)", because file retention (7 files) or the budget can exclude older entries. All four formats are read (`cmtrace`, `text`, `json`, `ecs-json`). When writing, `cmtrace` folds Error with Fatal and Info with Debug, flattens line breaks and caps messages at 3800 characters — "Critical" and full stack traces cannot be recovered from it. The text format carries no offset, so its time is read in the server's time zone.

Endpoints (admin): `GET /api/diagnostics/app-log` (`hours` 1–168, `minLevel`, `q`, `take` ≤ 1000; `minLevel` and `q` filter before `take`, `summary` always counts the whole window that was read) and `GET /api/diagnostics/app-log/download?date=yyyy-MM-dd`. The download is a merged, **redacted** daily export `nodepilot-app-<date>.redacted.log` (every segment of the day), not an unmodified copy of the file, and is recorded as `APP_LOG_DOWNLOADED` in the audit log. Output redaction works line by line; multi-line PEM blocks are removed statefully, unknown secret patterns remain possible.

## Security headers (non-development)

HSTS, CSP, `X-Frame-Options=DENY`, `nosniff`, `Referrer-Policy`.

## SignalR authentication

The httpOnly `np_auth` cookie is sent during the WebSocket upgrade (for `/hubs/` only); no `?access_token=` query string.
