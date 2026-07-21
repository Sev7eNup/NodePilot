# SIEM-Friendly Logging (ECS-JSON)

NodePilot ships an Elastic Common Schema 1.x JSON formatter for use with Elastic
Filebeat, Splunk HEC, Microsoft Sentinel, or any other SIEM that ingests structured
logs. Application logs and audit events flow through the same Serilog pipeline so a
single Filebeat sidecar covers both.

## Enabling

```jsonc
"Logging": {
  "Format": "ecs-json"
}
```

| Format value | Output | Use case |
|---|---|---|
| `text` (default) | plain output template | dev, console reading |
| `cmtrace` | ConfigMgr CMTrace XML-ish | ops on Windows, CMTrace.exe |
| `json` | Serilog Compact JSON (CLEF) | generic structured ingest |
| `ecs-json` | Elastic Common Schema 1.x | SIEM (Elastic / Sentinel / Splunk) |

## Wire format

One event per line, terminated by `\n`. Reserved ECS fields:

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

NodePilot-domain properties are namespaced under `nodepilot.*` (PascalCase →
`snake_case`). Adding new properties to a log event automatically lifts them into the
namespace; no formatter changes needed for new domain fields.

Properties whose name starts with one of the reserved ECS prefixes are hoisted to
the JSON root in their proper nested ECS shape rather than appearing under
`nodepilot.*`. Two prefix categories:

| Category | Prefixes | Example | Source |
|---|---|---|---|
| Host/service identity | `service.`, `host.`, `deployment.`, `agent.`, `cloud.`, `container.` | `service.name=nodepilot-api` | Serilog enricher (once per process). **Caveat:** the ECS file sink enriches only `service.name` + `deployment.environment` by default — `host.name` rides the OTLP/OTel path (`ResourceAttributes`), not the file stream; add an explicit enricher if you need `host.*` in the tailed file. |
| Per-event ECS fields | `event.`, `user.`, `source.`, `trace.`, `span.`, `error.`, `client.`, `network.`, `url.`, `http.` | `event.action=WORKFLOW_PUBLISHED`, `user.id=...`, `source.ip=10.1.2.3` | per-call from AuditWriter, request middleware, etc. |

Out-of-the-box SIEM detection rules (Sigma, Sentinel analytics, Elastic detection)
bind to these standard names; a rule that gates on `event.action == "LOGIN_FAILED"`
matches NodePilot data with no custom field-mapping pipeline.

**Duplicate-key dedup**: If two source-property names normalize to the same
snake_case target (e.g. `WorkflowId` and `workflow_id` both → `workflow_id`), the
formatter dedups via last-wins ordering. Several SIEM ingest pipelines reject
duplicate-key documents outright; pinning the behaviour explicitly keeps every
ingest pipeline consistent.

## Audit events on the SIEM stream

Every successful audit row is also emitted through Serilog as a structured event
with full ECS field coverage. The forwarded properties:

| ECS field | Source | Example |
|---|---|---|
| `event.action` | the action verb | `WORKFLOW_PUBLISHED`, `LOGIN_FAILED` |
| `event.category` | mapped from action prefix | e.g. `iam` (LOGIN/LOGOUT/TOKEN/USER/GLOBAL_VARIABLE/SECRETS), `process` (EXECUTION/WEBHOOK/TRIGGER lifecycle), `configuration` (workflow/machine mutations). Mapping in `AuditWriter`. |
| `event.kind` | constant | `event` |
| `event.outcome` | constant on success | `success` |
| `event.dataset` | constant | `nodepilot.audit` |
| `event.id` | the AuditLog row id | UUID — pivot key for joining back to the DB |
| `event.original` | redacted details JSON | `{"name":"Daily-Report","version":4}` |
| `user.id` | `ClaimTypes.NameIdentifier` | UUID |
| `user.name` | `ClaimTypes.Name` | `alice` |
| `source.ip` | `HttpContext.Connection.RemoteIpAddress` | `10.1.2.3` |

Resource-type and resource-id stay under `nodepilot.audit_resource_type` /
`nodepilot.audit_resource_id` because there is no good 1:1 ECS mapping.

Failed audit *writes* are also logged (at `error` level with the exception in the
`error` block) so a silently dropped audit row is visible operationally.

```json
{
  "@timestamp": "2026-05-09T...",
  "log.level": "info",
  "message": "audit.WORKFLOW_PUBLISHED resource=Workflow/abc... actor=user-42 ip=10.1.2.3",
  "ecs.version": "1.12.0",
  "event": {
    "action": "WORKFLOW_PUBLISHED",
    "category": "configuration",
    "kind": "event",
    "outcome": "success",
    "dataset": "nodepilot.audit",
    "id": "11111111-1111-...",
    "original": "{\"name\":\"Daily-Report\",\"version\":4}"
  },
  "user": { "id": "user-42", "name": "alice" },
  "source": { "ip": "10.1.2.3" },
  "nodepilot": {
    "audit_resource_type": "Workflow",
    "audit_resource_id": "abc..."
  }
}
```

Errors include a structured `error` block:

```json
{
  "log.level": "error",
  "message": "Step failed",
  "error": {
    "type": "System.InvalidOperationException",
    "message": "...",
    "stack_trace": "..."
  },
  "nodepilot": { "execution_id": "..." }
}
```

## Filebeat sample

```yaml
filebeat.inputs:
  - type: filestream
    id: nodepilot
    paths:
      - C:\ProgramData\NodePilot\logs\nodepilot-*.log
    parsers:
      - ndjson:
          target: ""        # already at the root
          add_error_key: true
processors:
  - timestamp:
      field: "@timestamp"
      layouts: ["2006-01-02T15:04:05.999999999Z07:00"]
output.elasticsearch:
  hosts: ["es.firma.de:9200"]
  index: "nodepilot-%{+yyyy.MM.dd}"
```

## Splunk HEC sample

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

## What is NOT in this version

- **No Audit-Outbox push pipeline yet.** AuditLog table rows are still queryable via
  `GET /api/audit`; SIEM ingest of audit events requires Filebeat reading the same
  ECS log file (Audit-Writer events render through Serilog when Logging:Format=ecs-json).
  A dedicated outbox + push-sink with retry / backpressure is on the roadmap
  (`enterprise-siem-logging.md`).
- **No CEF / LEEF format.** ArcSight / QRadar customers can wait for the V2 plan.
- **No syslog sink.** Add via Filebeat or a Serilog.Sinks.Syslog package separately.

## Operator checklist

- [ ] `Logging:Format=ecs-json` set in `appsettings.Production.json`.
- [ ] Filebeat / Vector / Fluentd installed alongside the NodePilot service.
- [ ] Log file path matches `Logging:File:Path` (code default `<ContentRoot>/logs/nodepilot-.log`; the production installer points it at `C:\ProgramData\NodePilot\logs\nodepilot-.log`).
- [ ] One sample event indexed end-to-end in the SIEM (smoke test).
- [ ] At least one dashboard panel created on `nodepilot.execution_id` to validate the namespace.
