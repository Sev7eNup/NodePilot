# Alerting (Notification-Rules)

User-authored rules that deliver notifications through one or more channels when a matching event
occurs. **Opt-in by data** — the dispatcher is always registered but does nothing until at least one
`NotificationRule` exists. Covers execution events, live execution-state events, and signal events
(service/machine/backlog/schedule/workflow-health), delivered via the Email + generic-webhook channels.

## Concepts

A **rule** answers: *"when an event of one of these `EventTypes` happens, that is in this `Scope`,
and matches this optional `Filter`, deliver via these `Routes` — unless suppressed by cooldown/flap."*

| Piece | Meaning |
|---|---|
| `EventTypes` | Coarse pre-filter: comma-separated `NotificationEventType` names. Execution/workflow-scoped: `ExecutionFailed`, `ExecutionSucceeded`, `ExecutionCancelled`, `ExecutionRunningLong`, `ExecutionQueuedLong`, `ScheduleMissed`, `WorkflowNoRecentSuccess`, `CredentialFailure`. Global signals: `ServiceStale`, `MachineUnreachable`, `BacklogHigh`, `PendingHigh`, `CancelRateHigh`. |
| `ScopeKind` | `Global` (all workflows), `Folders` (rule's folder targets), or `Workflows` (rule's workflow targets). |
| `Filter` | Optional composable AND/OR/NOT expression over event fields — the **same condition AST** the designer uses for edge conditions, with operands of `source: "event"`. Empty = match every event of the configured types in scope. |
| `DedupKeyTemplate` | Optional grouping template for cooldown/flap state, e.g. `{{eventType}}:{{workflowId}}`; empty uses the default workflow/source grouping. |
| `Routes` | One delivery target each: a `Channel` (`Email` / `GenericWebhook`) + a `Target` (recipient / URL) + an optional encrypted `Secret` (webhook HMAC). Each route can also carry its own event-field condition, so one rule may notify different channels for different severities or thresholds. |
| Throttle | `CooldownMinutes` (rate-limit per dedup key), `MinOccurrences` + `OccurrenceWindowMinutes` (flap suppression). |

## Data model (`NodePilot.Core.Models`)

- `NotificationRule` (+ `NotificationRoute`, `NotificationRuleTarget`) — the rule and its children
  (cascade-delete). Targets are soft references (no hard FK) to folders/workflows, like
  `MaintenanceWindowTarget`.
- **State tables, kept separate from the rule on purpose:**
  - `NotificationSuppressionState` — cooldown/flap state, one row per `(RuleId, DedupKey)`. Answers
    "may this rule fire again for this key yet?".
  - `NotificationDeliveryAttempt` — the per-occurrence, per-route ledger **and** the idempotency
    guard, unique on `(RuleId, RouteId, EventKey)`. `IsTest` marks test-fires (they never touch
    suppression).
  - `NotificationSignalState` — last-seen health state per non-execution signal source
    (`service:{name}`, `backlog`, …): `healthy`, `unhealthy` (an observed degradation → alertable
    episode), or `unhealthy-seeded` (already unhealthy at first sight → silent, never alerts). The
    gauge collector tracks transitions here and uses the episode-start timestamp as the per-occurrence
    idempotency key.
  - `NotificationDispatcherState` — single-row watermark (`LastCompletedAtSeen` + `LastIdSeen`) for
    the execution-scan cursor.
- Store `INotificationRuleStore` → `NotificationRuleStore` (`NodePilot.Data`). Route secrets are
  encrypted at rest via `ISecretProtector`; an `__unchanged__` sentinel lets an edit keep a stored
  secret without re-sending it. `UpdateAsync` **diffs** routes (matched-by-id updated in place, new
  added via the `DbSet`, dropped removed) so the EF ChangeTracker never sees a duplicate key, and
  `DeleteAsync` also clears the orphan suppression + attempt rows (no FK from those to the rule).
- Migration: `AddNotificationRules` (provider-agnostic; `type:` annotations stripped per repo rule).

## Matching + dispatch

`NodePilot.Scheduler.NotificationDispatcher` — a **leader-gated** `BackgroundService` (~30 s cadence),
so in an HA cluster only the leader dispatches. Each pass:

1. **Recover** Pending delivery attempts orphaned by a crash — each collector reconstructs the
   contexts of its own `EventKey` form via `TryReconstructContextAsync` (if the referenced
   execution is gone, the attempt is failed out).
2. **Collect** terminal executions since the persisted watermark. On the very first run the watermark
   is **seeded to "now"** so existing history is never back-alerted; thereafter it advances by
   `(CompletedAt, Id)`.
3. **Match** each event against enabled rules: event-type pre-filter → scope → filter AST (via the
   shared `ConditionEvaluator`, `EventFields` = `NotificationContext.ToFieldMap()`) → route-local filters.
4. **Suppress** via cooldown (skip if within `CooldownMinutes` of the dedup key's last fire) and flap
   (`MinOccurrences` within `OccurrenceWindowMinutes`).
5. **Persist** a Pending `NotificationDeliveryAttempt` per route **before any network I/O**
   (idempotent on `(RuleId, RouteId, EventKey)`), advance the watermark in the same save, **then**
   send. A crash between persist and send leaves a replayable Pending row → at-least-once delivery,
   exactly-once per `(rule, route, occurrence)`.

The dispatcher writes a `SystemHealthWriter` heartbeat (`NotificationDispatcher`) each pass, so the
dashboard's service-freshness view tracks it like the other scheduler services. Execution and gauge
contexts share one `MatchAndSendAsync` pipeline (match → suppress → persist-Pending → send).

### Signal events → system policies (ADR 0008)

State-degradation alerts (backlog/pending depth, cancel rate, machine reachability, service-heartbeat
staleness, credential expiry, schedule-missed, no-recent-success) are **no longer** a gauge collector. The
legacy `GaugeSignalCollector` + eight `IGaugeSignalProvider`s were **removed** once the modular
`ISystemAlertSource` catalog covered them; those signals are now **system policies** — see the
[System alerts](#system-alerts--modular-sources-adr-0008-in-progress) section below, which lists the 12
sources and how a policy sets its own threshold/duration/severity/scope. Their `NotificationEventType`
values (`ServiceStale`, `BacklogHigh`, …) remain as an append-only persisted contract but were dropped from
`SupportedEventTypes`, so a **custom** rule no longer reacts to them (only execution-family events do).

### Live execution-state events

A third collector (`LongRunningExecutionCollector`) alerts on a **still-running** execution that has been
running longer than `Alerting:LongRunningSeconds` (default 600) — a **live** signal, unlike the
after-the-fact `durationMs` filter on terminal events. It scans `Running` rows and emits one context
per execution keyed `runlong:{executionId}`; the per-`(rule,route,EventKey)` existence-check makes each
execution alert **exactly once** (no re-alert every pass, no per-execution signal-state row to leak).
Unlike gauge events it is **execution-scoped** — it carries a real `WorkflowId`, so a rule may be
`Global`, `Folders`, or `Workflows`. `durationMs` = elapsed ms. The scan is skipped when no enabled rule
references `ExecutionRunningLong`.

`ExecutionQueuedLong` is the pending-side companion: it scans `Pending` rows older than
`Alerting:QueuedLongSeconds` (default 300), emits one workflow-scoped context keyed
`queuedlong:{executionId}`, and sets `status = Pending` plus elapsed `durationMs`.

`CredentialFailure` is derived from terminal failed executions whose error message indicates an
authentication/credential problem (for example access denied, unauthorized, logon failure, invalid
password, Kerberos/NTLM). It is emitted in addition to the normal `ExecutionFailed` event, with its own
`EventKey` (`exec:{executionId}:CredentialFailure`), so generic failure rules and credential-specific
rules can coexist.

### Event fields (`source: "event"`)

The filter AST resolves `event` operands against `NotificationContext.ToFieldMap()`. Keys (mirrored on
the frontend `lib/eventFields.ts` `EVENT_FIELD_CATALOG`, which is **adaptive**: global-only signal rules
show signal fields, workflow-scoped signal rules show both workflow context and signal fields):

`eventType`, `severity`, `workflowName`, `folderPath`, `status`, `errorMessage`, `durationMs`,
`triggeredBy`, `callDepth`, `isSubWorkflow`, `cancelledBy` (execution/workflow context) · `sourceKey`,
`signalValue` (signal context) · `targetMachine` (both: gauge signals carry the signal source's
machine name; terminal execution events carry the resolved machine name of the **last-failing step**
— empty for successes/cancels and for failures without a remote target).

`cancelledBy` is set only for `ExecutionCancelled` events and records who initiated the cancel:
`user` (a single manual `POST /executions/{id}/cancel`), `cancelAll`, `failover`, `reconciler`,
`dispatch`, or `system` (a timeout / host-shutdown / bare-token cancel). A rule targeting **manual**
cancels filters `cancelledBy == "user"`.

Implementation: the value is persisted in `WorkflowExecution.CancelledBy` (migration
`AddExecutionCancelledBy`), set in every cancel path. For a live cancel the engine thread writes the
terminal status inside its `catch (OperationCanceledException)` — attribution is threaded in via
`IWorkflowEngine.CancelAsync(id, cancelledBy)` → an internal `_cancelReasons` dictionary that the
catch block reads.

## Sinks (`NodePilot.Engine.Notifications`)

`INotificationSink` (one per `NotificationChannel`); resolved by the dispatcher + the test-fire
endpoint. Self-isolating — any failure returns a failed result, never throws, so one bad route can't
break a dispatch pass.

- **`SmtpNotificationSink`** (Email) — reuses the configured `SmtpOptions` and the same hardening as
  `EmailActivity`: default-on TLS, single recipient (no comma/semicolon lists), header-injection
  guard, bounded send timeout.
- **`WebhookNotificationSink`** (GenericWebhook) — POSTs the rendered JSON through the SSRF-guarded
  `"NodePilot"` named `HttpClient` (URL validated up front by `NetworkGuard.ValidateUrl` and again at
  TCP-connect time). If the route carries a secret, the body is signed with HMAC-SHA256 in an
  `X-NodePilot-Signature: sha256=…` header.
- `NotificationRenderer` (pure) builds the title / plain-text email body / webhook JSON — separated so
  payload shape is unit-testable without SMTP/HTTP.

> Follow-up: PagerDuty / Opsgenie channels can reuse this sink + signal-state machinery.

## Governance & security

- **Authorization:** read is Admin/Operator; create/edit/delete/enable/disable/test-fire is **Admin-only**
  (mirrors `MaintenanceWindowsController`).
- **Secrets:** route secrets are encrypted at rest and **redacted in every API response** (the
  unchanged-sentinel, never the cipher). Webhook URLs are SSRF-filtered; email is single-recipient +
  header-injection-guarded.
- **Audit:** `ALERT_RULE_CREATED`, `ALERT_RULE_UPDATED`, `ALERT_RULE_DELETED`, `ALERT_RULE_ENABLED`, `ALERT_RULE_DISABLED`, `ALERT_RULE_TEST_FIRED`.

## Surfaces

### REST API (`/api/alerting`)

| Endpoint | Role | Notes |
|---|---|---|
| `GET /rules`, `GET /rules/{id}` | Admin/Op | Routes redacted. |
| `GET /deliveries` | Admin/Op | Delivery ledger (newest first); optional `?ruleId=`/`?status=`/`?limit=` (max 500). No secrets — only channel + target. |
| `POST /rules`, `PUT /rules/{id}`, `DELETE /rules/{id}` | Admin | New rules default to **disabled** (opt-in). |
| `POST /rules/{id}/enable`, `POST /rules/{id}/disable` | Admin | Per-row toggle in the UI actions — flips `IsEnabled` without re-submitting the full draft; kind-scoped (a system-policy id 404s). |
| `POST /rules/{id}/test-fire` | Admin | Sends a synthetic notification through every route now; returns per-route success; records `IsTest` attempts. |
| `POST /preview-filter` | Admin/Op | Stateless dry-run: does a filter expression match a sample event-field map? |
| `GET /catalog` | Admin/Op | Lists supported event types, event fields, channels, and dedup-template fields for clients/builders. |
| `POST /preview-rule` | Admin/Op | Stateless dry-run of a whole rule against sample event fields; returns rule match, rendered dedup key, and route match results. |

### UI (`/alerts`)

`AlertingPage` (list + search + role-gated CRUD) and `AlertingRuleEditor` — a single-page form
(name/events → scope → filter → routes → throttle) with an inline **test-fire**. The filter editor is
the designer's **`ConditionBuilder`** reused via a new optional `eventFields` prop (event-field
operand mode; the globals query is disabled in that mode). A **Deliveries** button opens the
`DeliveriesModal` — the delivery ledger (recent attempts, status filter). i18n namespace `alerts` (DE/EN).

### CLI

```
np alerting list
np alerting get <rule-id>
np alerting create --name "Prod failures" --event-types ExecutionFailed,ExecutionCancelled \
                   --email ops@example.com --webhook https://hooks.example.com/np --cooldown-minutes 30
np alerting update <rule-id> --name "Renamed"          # read-modify-write; unset fields preserved
np alerting delete <rule-id>
np alerting test-fire <rule-id>                         # prints per-route results; non-zero exit if any failed
np alerting deliveries --status Failed                 # delivery ledger; filter by --rule / --status / --limit
```

Routes are given with `--email` / `--webhook` (repeatable); scope targets with `--folder` /
`--workflow`.

### MCP

`list_alerting_rules`, `get_alerting_rule`, `list_alerting_deliveries`, `create_alerting_rule`,
`update_alerting_rule`, `test_fire_alerting_rule` (read + write); `delete_alerting_rule` lives in the
gated `DestructiveTools` (only registered when `NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true`; listed in
`get_safety_status`). Route secrets are never surfaced in tool output.

## System alerts — modular sources (ADR 0008, in progress)

A second alerting generation is being built alongside the rules above: a **catalog-driven** system-alert
area where each built-in producer (backlog, machine health, execution results, …) is a self-describing
**source**, and one source can carry several independent **policies** with their own threshold, sustain
window, scope, and routes. See [ADR 0008](adr/0008-modular-system-alert-sources.md) for the model
(source / observation / policy, source-vs-policy state separation, one-pipeline decision, phasing) and the
rationale for choosing compiled modules over a text query language.

The catalog currently ships **12** sources: `backlog`, `pending`, `cancel-rate`, `machine-unreachable`,
`service-stale`, `credential-expiring`, `workflow-no-recent-success`, `schedule-missed`, `execution-result`,
`execution-stuck` (live-hang detection), `workflow-health` (rolling failure-rate / p95 from `WorkflowStats`),
and `alert-delivery-failed` (self-monitoring: the alarm about broken alarms).

Delivered so far (foundation phase, non-destructive — the custom rules above are unchanged):

- The `ISystemAlertSource` seam (`NodePilot.Scheduler.SystemAlerts`): each source publishes a pure-metadata
  `SystemAlertSourceDescriptor` (`NodePilot.Core.Models`) — stable `SourceId`, category, scope capability,
  default severity, a field schema (name / type / unit / operators), query parameters, and presets — and
  yields raw `SystemAlertObservation`s without deciding health.
- `SystemAlertCatalog` aggregates the DI-registered sources and enforces unique/consistent source ids at boot.
- Read-only endpoint `GET /api/alerting/system/catalog` (Admin/Operator) — the single server-owned source of
  truth the UI will localize and render from (i18n stays a frontend concern; descriptors carry keys, not text).
- Additive schema (migration `AddSystemAlertPolicies`): `NotificationRule` gains `Kind` (existing rows
  backfilled `Kind=Custom` — nothing is wiped), `SystemSourceId`, `SystemPresetId`, `SourceParametersJson`,
  `SustainForSeconds`, `SeverityOverride`, `ActivatedAt`, plus the `SystemAlertPolicyStates` /
  `SystemAlertSourceStates` tables.
- `SystemAlertEvaluator` (in `NotificationDispatcher`): each pass it groups enabled `Kind=System` policies by
  source + normalized parameters, samples each source once, and runs each policy's condition + sustain window
  against every applicable observation — keeping per-(policy, source, instance) match/episode state and
  emitting through the shared suppress → persist-Pending → send pipeline (episode-keyed `system:` EventKeys,
  exactly-once + crash-recovery reconstruction). Event sources honour a per-policy `ActivatedAt` watermark so
  a late-activated policy never back-alerts history; recovery is silent (no "resolved" alert in v1).
  `NotificationRetentionService` prunes stale `SystemAlertPolicyStates` (unobserved instances), and the
  evaluator drops state for disabled/removed policies each pass.
- A new `NotificationEventType.SystemAlert` family carries every system alert (the producer is identified by
  `NotificationContext.SourceId`, not a per-source enum value) — deliberately excluded from
  `SupportedEventTypes`, so the custom-rule surface never offers it.
- REST under `/api/alerting/system` (Admin/Operator read; Admin-only mutate — audited
  `SYSTEM_ALERT_POLICY_*`): `GET /catalog`, `GET/POST /policies`, `GET/PUT/DELETE /policies/{id}`,
  `POST /policies/{id}/enable|disable`, `POST /preview` (stateless — samples the source now and reports which
  current instances match; rate-limited `alerting-heavy`), `POST /policies/{id}/test-fire` (route delivery
  test, writes `IsTest` attempts; rate-limited). Kind isolation is enforced both ways: the custom
  `/api/alerting/rules` endpoints only see/mutate `Kind=Custom`, the system endpoints only `Kind=System`.
- A strict alert-specific AST validator (`SystemAlertConditionValidator`) gates a policy's condition at save:
  known fields, operator/type compatibility, operand arity, numeric-literal parseability, depth/node/regex
  caps — returning field-level RFC-7807 (`ValidationProblemDetails`) errors. It does **not** touch the
  permissive workflow-edge `ConditionEvaluator`.
- Source query parameters are descriptor-validated (declared names, required present, numeric bounds) and
  stored as `SourceParametersJson`. Enabling a policy stamps `ActivatedAt`; changing a policy's
  source/params/filter/scope/duration resets its transient state.

- UI: `/alerts` is split into two tabs — **System alerts** (default) renders the server catalog as cards
  grouped by category, each showing per-source status (Not configured / Active / Disabled / Unavailable) and
  its policies with inline enable/disable/edit/delete; **Custom rules** keeps the existing table. The
  descriptor-driven `SystemPolicyEditor` builds the condition with the shared `ConditionBuilder` (fed the
  source's fields), renders typed source-parameter inputs, sustain/severity/scope/cooldown/routes, a preset
  picker, and a live "check current values" preview. Catalog hydrated via `useSystemAlertCatalog`
  (React-Query, 60s staleness). New i18n keys under the `alerts:system.*` namespace (DE + EN).

All phases are delivered: REST/UI split + strict AST validator, CLI (`np system-alert`) + MCP tools, backup
format v2 (the `alerting` section), and the one-pipeline end state — the legacy gauge collector/providers
were removed once the sources covered them (see the "Signal events → system policies" note above). The only
deliberately-kept vestige is the now-unused `NotificationSignalState` table (dropping it would need a
destructive migration).

## Follow-ups

- Channels: PagerDuty / Opsgenie.
- Optional "resolved" recovery notifications for signal events.
- Escalation policies; per-rule custom dedup-key templates.
- `sourceKey` event field populated for execution events (`targetMachine` is populated since v1c —
  joined from the last-failing step).

## Delivery ledger & retention

Every send writes a `NotificationDeliveryAttempt` row (the exactly-once guard + the audit trail of
what fired). A **bounded retry** keeps a failed attempt `Pending` and retries it on later dispatcher
passes until `MaxAttempts` (5), then marks it `Failed`. The ledger is read via `GET /api/alerting/deliveries`
(UI **Deliveries** modal, `np alerting deliveries`, MCP `list_alerting_deliveries`). To keep the table
from growing unbounded, `NotificationRetentionService` (leader-gated, config `Retention:Notifications:*`,
default 90 days / every 6 h, opt-out via `Enabled:false`) deletes terminal attempts past the cutoff and
prunes stale suppression rows — never a `Pending` row (in-flight retries are safe).
