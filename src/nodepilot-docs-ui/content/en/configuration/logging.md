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

`OutputRedactor` masks secrets. **Always active.** Custom patterns via `Logging:Redaction:Patterns`.

## Support log & SupportEvents

Two sub-sinks from the same Serilog filter (for operator/ticket diagnosis):

1. **A plain-text file** at `{Logging:SupportLog:Path}` (production: `C:\ProgramData\NodePilot\logs\nodepilot-support-*.log`). `Logging:SupportLog:RetainedFileCountLimit` (default `90`) limits the **number** of retained files, not the number of days: with daily rollover that corresponds to roughly 90 days, but `Logging:SupportLog:FileSizeLimitBytes` (default 10 MiB) additionally rolls within a day — on very talkative systems the limit therefore covers correspondingly fewer days.
2. **The database table `SupportEvents`** for the web viewer (filter/cursor/export) — toggled by `Logging:SupportLog:DbProjectionEnabled` (default `true`). Written through the buffered `SupportEventFlushService`, trimmed by `SupportEventRetentionService` (90 d).

Endpoints: `GET /api/diagnostics/support-log|support-log/download|support-events|support-events/export` (admin). UI: its own main-menu page `/support-log` (admin only, in the sidebar under "Alerting").

## Security headers (non-development)

HSTS, CSP, `X-Frame-Options=DENY`, `nosniff`, `Referrer-Policy`.

## SignalR authentication

The httpOnly `np_auth` cookie is sent during the WebSocket upgrade (for `/hubs/` only); no `?access_token=` query string.
