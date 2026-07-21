# SIEM-Logging (ECS-JSON)

NodePilot shippt einen Elastic Common Schema 1.x JSON-Formatter für Elastic Filebeat, Splunk HEC, Microsoft Sentinel oder jedes andere SIEM, das strukturierte Logs ingestet. Application-Logs und Audit-Events fließen durch dieselbe Serilog-Pipeline — ein Filebeat-Sidecar deckt beides.

## Aktivieren

```jsonc
"Logging": { "Format": "ecs-json" }
```

| Format-Wert | Output | Use Case |
|---|---|---|
| `text` (default) | Plain-Template | Dev, Console |
| `cmtrace` | ConfigMgr CMTrace | Ops auf Windows |
| `json` | Serilog Compact JSON (CLEF) | Generic structured ingest |
| `ecs-json` | Elastic Common Schema 1.x | SIEM (Elastic/Sentinel/Splunk) |

## Wire-Format

Ein Event pro Zeile, `\n`-terminiert. Reservierte ECS-Fields:

```json
{
  "@timestamp": "2026-05-08T22:34:11.482Z",
  "log.level": "info",
  "message": "Workflow execution started",
  "ecs.version": "1.12.0",
  "nodepilot": {
    "workflow_id": "...",
    "execution_id": "...",
    "step_id": "step-7",
    "duration": 1234
  }
}
```

NodePilot-Domain-Properties landen unter `nodepilot.*` (PascalCase → `snake_case`). Properties mit ECS-Prefix (`event.`, `user.`, `source.`, `service.`, `host.`, `error.`, `trace.`, `http.`, `url.`, `network.` …) werden an den JSON-Root in ihrer geschachtelten ECS-Form gehoben.

**Duplicate-Key-Dedup:** zwei Source-Namen, die aufs gleiche snake_case-Target normalisieren, werden per last-wins dedupliziert — verhindert Rejects bei strict-Ingest-Pipelines.

## Audit-Events auf dem SIEM-Stream

Jeder erfolgreiche Audit-Row wird zusätzlich als strukturiertes ECS-Event emittiert:

| ECS-Field | Source | Beispiel |
|---|---|---|
| `event.action` | Action-Verb | `WORKFLOW_PUBLISHED` |
| `event.category` | aus Action-Prefix gemappt | `iam` (Login/User/Credential), `process` (Execution/Trigger), `configuration` (Workflow/Machine) |
| `event.kind` | konstant | `event` |
| `event.outcome` | konstant bei Erfolg | `success` |
| `event.dataset` | konstant | `nodepilot.audit` |
| `event.id` | AuditLog-Row-ID | UUID |
| `event.original` | redacted Details-JSON | `{"name":"Daily-Report","version":4}` |
| `user.id` / `user.name` | Claims | UUID / `alice` |
| `source.ip` | `RemoteIpAddress` | `10.1.2.3` |

Out-of-the-box Sigma/Sentinel/Elastic-Detection-Rules matchen ohne Custom-Field-Mapping. Fehlschlagende Audit-*Writes** werden ebenfalls geloggt (`error`-Level mit `error`-Block), damit eine still gedroppte Audit-Row operationell sichtbar wird.

## Filebeat-Beispiel

```yaml
filebeat.inputs:
  - type: filestream
    id: nodepilot
    paths:
      - C:\ProgramData\NodePilot\logs\nodepilot-*.log
    parsers:
      - ndjson:
          target: ""
          add_error_key: true
processors:
  - timestamp:
      field: "@timestamp"
      layouts: ["2006-01-02T15:04:05.999999999Z07:00"]
output.elasticsearch:
  hosts: ["es.firma.de:9200"]
  index: "nodepilot-%{+yyyy.MM.dd}"
```

## Splunk HEC

```yaml
filebeat.inputs:
  - type: filestream
    paths: [C:\ProgramData\NodePilot\logs\*.log]
    parsers: [{ ndjson: {} }]
output.http:
  url: https://splunk.firma.de:8088/services/collector/event
  headers:
    Authorization: "Splunk {{HEC_TOKEN}}"
```

## Operator-Checkliste

- [ ] `Logging:Format=ecs-json` in `appsettings.Production.json`.
- [ ] Filebeat / Vector / Fluentd neben dem NodePilot-Service installiert.
- [ ] Log-File-Path matched `Logging:File:Path` (Produktion: `C:\ProgramData\NodePilot\logs\nodepilot-.log`).
- [ ] Ein Sample-Event end-to-end im SIEM indexed (Smoke-Test).
- [ ] Mind. ein Dashboard-Panel auf `nodepilot.execution_id` erstellt.

## Out of scope

Audit-Outbox-Push-Pipeline (V2), CEF/LEEF, Syslog-Sink (via Filebeat oder `Serilog.Sinks.Syslog` addierbar).