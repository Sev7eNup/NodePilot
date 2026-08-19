# SIEM logging (ECS-JSON)

NodePilot can emit application logs and audit events as Elastic Common Schema 1.x. The ECS-JSON format suits Elastic Filebeat, Splunk HEC, Microsoft Sentinel and other SIEM systems with structured JSON ingest. Both event kinds use the same Serilog pipeline.

## Enabling it

```jsonc
"Logging": { "Format": "ecs-json" }
```

| Format value | Output | Use case |
|---|---|---|
| `text` (default) | A plain template | Development, console |
| `cmtrace` | ConfigMgr CMTrace | Operations on Windows |
| `json` | Serilog compact JSON (CLEF) | Generic structured ingest |
| `ecs-json` | Elastic Common Schema 1.x | SIEM (Elastic/Sentinel/Splunk) |

## Wire format

One event per line, `\n`-terminated. Reserved ECS fields:

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

NodePilot domain properties land under `nodepilot.*` (PascalCase → `snake_case`). Properties with an ECS prefix (`event.`, `user.`, `source.`, `service.`, `host.`, `error.`, `trace.`, `http.`, `url.`, `network.` …) are lifted to the JSON root in their nested ECS form.

**Duplicate-key deduplication:** two source names that normalize to the same snake_case target are deduplicated last-wins — this prevents rejections in strict ingest pipelines.

## Audit events on the SIEM stream

Every successful audit row is additionally emitted as a structured ECS event:

| ECS field | Source | Example |
|---|---|---|
| `event.action` | The action verb | `WORKFLOW_PUBLISHED` |
| `event.category` | Mapped from the action prefix | `iam` (login/user/credential), `process` (execution/trigger), `configuration` (workflow/machine) |
| `event.kind` | Constant | `event` |
| `event.outcome` | Derived from the action and the structured `details.success` | `success`, `failure`, or `unknown` for `*_ATTEMPTED` |
| `event.dataset` | Constant | `nodepilot.audit` |
| `event.id` | The audit-log row ID | UUID |
| `event.original` | The redacted details JSON | `{"name":"Daily-Report","version":4}` |
| `user.id` / `user.name` | Claims | UUID / `alice` |
| `source.ip` | `RemoteIpAddress` | `10.1.2.3` |

Out-of-the-box Sigma/Sentinel/Elastic detection rules match without custom field mapping. Failing audit **writes** are logged as well (at `error` level with an `error` block), so that a silently dropped audit row becomes operationally visible.

## Filebeat example

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
  hosts: ["es.example.com:9200"]
  index: "nodepilot-%{+yyyy.MM.dd}"
```

## Splunk HEC

```yaml
filebeat.inputs:
  - type: filestream
    paths: [C:\ProgramData\NodePilot\logs\*.log]
    parsers: [{ ndjson: {} }]
output.http:
  url: https://splunk.example.com:8088/services/collector/event
  headers:
    Authorization: "Splunk {{HEC_TOKEN}}"
```

## Operator checklist

- [ ] `Logging:Format=ecs-json` in `appsettings.Production.json`.
- [ ] Filebeat / Vector / Fluentd installed alongside the NodePilot service.
- [ ] The log file path matches `Logging:File:Path` (production: `C:\ProgramData\NodePilot\logs\nodepilot-.log`).
- [ ] One sample event indexed end to end in the SIEM (smoke test).
- [ ] At least one dashboard panel created on `nodepilot.execution_id`.

## Out of scope

An audit outbox push pipeline (V2), CEF/LEEF, a syslog sink (which can be added through Filebeat or `Serilog.Sinks.Syslog`).
