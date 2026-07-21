# ADR 0008 - Modular System-Alert Sources, Observations & Policies

**Status:** Accepted — 2026-07-10
**Scope:** Alerting subsystem — how NodePilot models the built-in alert producers (backlog, machine
health, execution results, …) and how operators configure alerting on top of them.

## Kontext

NodePilot's first alerting generation ([docs/alerting.md](../alerting.md)) grew a `NotificationRule`
delivery substrate fed by two kinds of producer: four `INotificationCollector`s (terminal executions,
long-running, queued-long, gauge signals) and eight `IGaugeSignalProvider`s behind the gauge collector.
That shape has three structural limits that block the product we want:

1. **The producer decides "unhealthy," not the operator.** Every `IGaugeSignalProvider` hard-codes a
   single threshold from `Alerting:Gauge:*` config and emits a healthy→unhealthy episode. An operator
   cannot say "warn at 200, page at 500" — a source has exactly one live threshold, and a rule can only
   filter *within* the episode the provider already decided to open.
2. **Thresholds are global runtime config, not per-policy data.** `Alerting:Gauge:BacklogThreshold`
   is re-read every dispatcher pass and is a live gate for *all* backlog rules at once. There is no way
   to attach a threshold, a sustain duration, or a lookback window to an individual rule.
3. **The field/type/operator metadata is split-brained.** The API `GET /api/alerting/catalog` hand-lists
   fields, while the frontend `lib/eventFields.ts` hand-lists them again; a parity test checks *names*
   but not classification, and the two already disagree on `targetMachine` (`gauge` vs `both`).

We want a modular, catalog-driven alerting area where each built-in producer is a self-describing unit,
one producer can carry several independent policies (own threshold/duration/scope/route), and the field
schema has a single server-owned source of truth.

## Entscheidung

Introduce three code-owned concepts and one central evaluator.

- **System Alert Source** (`ISystemAlertSource`, `NodePilot.Scheduler.SystemAlerts`). A compiled,
  DI-registered module — **not** a user-uploadable plugin and **not** a text query language (no AlertQL /
  SQL / PromQL). Each source publishes a pure-metadata `SystemAlertSourceDescriptor`
  (`NodePilot.Core.Models`): a stable `SourceId`, a category, scope capability, default severity, a
  **field schema** (name / type / unit / operators), **query parameters** (e.g. a cancel-rate lookback
  window), and **presets**. A source reports measurements/events but **never decides severity or health**.

- **System Alert Observation** (`SystemAlertObservation`, `NodePilot.Core.Models`). One raw sample from a
  source: `SourceId`, a stable `InstanceKey` (per credential / per workflow+node / per execution / a
  constant for global singletons), optional scope identity, a severity *suggestion*, title/summary/deep-link,
  and normalized field values. The evaluator — not the source — applies each policy's condition and sustain
  window to observations.

- **System Alert Policy.** A configured decision on top of a source: source + optional preset, a strict
  descriptor-validated condition AST, descriptor-validated source parameters, a sustain duration, severity,
  scope, throttle, and routes. Policies are stored on the existing `NotificationRule` table
  (`Kind = System`) so they reuse the proven, crash-safe delivery pipeline (persist-Pending-before-send,
  the `(RuleId, RouteId, EventKey)` exactly-once ledger, leader-gated dispatch, cooldown/flap suppression).

**One pipeline, not two.** The eight legacy `IGaugeSignalProvider`s and the `GaugeSignalCollector` were
**removed** once the `ISystemAlertSource` catalog covered them (12 sources today). Infra/signal alerts —
backlog, pending, cancel-rate, machine health, service staleness, credential expiry, schedule-missed,
no-recent-success — are now **system policies only**; their `NotificationEventType` values remain
(append-only persisted contract) but were dropped from `NotificationRuleSemantics.SupportedEventTypes`, so
the custom-rule surface no longer offers them. What stays on the custom side are the execution-family
collectors (terminal executions, long-running, queued-long) that a free-filter rule reacts to. Running a
gauge path beside a source path over the *same* tables would have doubled DB load and given the same
condition two different episode/threshold semantics — so the dual path was a deliberately time-boxed
transition, now closed. (Update, 2026-07-10: legacy gauge path removed — the one-pipeline end state is reached.)

**Source vs. policy state are separated.** A source owns coarse cursors in `SystemAlertSourceStates`
(unique per `(SourceId, StateKey)` — e.g. the terminal-execution scan watermark). A policy owns transient
match/duration/episode state in `SystemAlertPolicyStates` (unique per `(NotificationRuleId, SourceId,
InstanceKey)`). Because the source cursor is shared across a source's policies, event sources additionally
gate on a **per-policy activation watermark** so a policy activated later never back-alerts the history a
sibling policy already advanced the shared cursor past.

**Automatic state reset.** Disabling a policy, or changing its source, source parameters, filter, scope,
or duration, drops its transient policy state (config and routes survive). First activation never
back-alerts: event sources start from the activation instant; metric sources start their sustain window
from the first current observation; an unavailable source yields neither alert nor recovery.

**Localization stays a frontend concern.** Descriptors carry stable ids/keys and structure (fields, types,
units, operators, defaults) — **not** display text. The UI localizes via `react-i18next` keys derived from
`SourceId`/field `Name` in the `alerts` namespace, so DE/EN parity remains a frontend guard, and there is
no backend i18n store to stand up.

## Konsequenzen

- The catalog gains one server-owned source of truth. The static frontend field catalog and its
  name-only parity test are replaced by hydrating from `GET /api/alerting/system/catalog`; the parity
  guard is rewritten to assert i18n keys cover the server catalog rather than deleted.
- Delivered in phases so each step is reviewable and non-destructive until the last: **(this PR)** the
  seam + descriptors + read-only `GET /api/alerting/system/catalog`, no schema change; then additive
  `NotificationRule` columns (`Kind`, `SystemSourceId`, …, nullable, existing rows backfilled
  `Kind=Custom`) + the two state tables + the evaluator; then the REST/UI split + strict AST validator;
  then CLI + MCP parity (incl. destructive-tool gating); then backup format v2; and any destructive
  cleanup migration ships **last and separately**. Existing custom rules are **preserved** (backfilled),
  not wiped.
- New durable per-instance state (`SystemAlertPolicyStates`) must be pruned — `NotificationRetentionService`
  gains a stale-instance sweep, and policy state is persisted on transition, not every pass.
- A strict alerting-only AST validator (known fields, operator/type compatibility, depth/node/regex caps,
  field-level RFC-7807 errors) is added **without** changing the permissive workflow-edge
  `ConditionEvaluator` semantics.
- New audit actions (`SYSTEM_ALERT_POLICY_*`), Admin-only mutation RBAC (reads Admin/Operator), and a
  rate-limit on the live-observation `preview` / outbound `test-fire` endpoints are required by the later
  phases.

## Referenzen

- Seam & registry: [`ISystemAlertSource`](../../src/NodePilot.Scheduler/SystemAlerts/ISystemAlertSource.cs),
  [`SystemAlertCatalog`](../../src/NodePilot.Scheduler/SystemAlerts/SystemAlertCatalog.cs).
- Metadata types: [`SystemAlertSourceDescriptor`](../../src/NodePilot.Core/Models/SystemAlertDescriptor.cs),
  [`SystemAlertObservation`](../../src/NodePilot.Core/Models/SystemAlertObservation.cs),
  [`SystemAlertEnums`](../../src/NodePilot.Core/Enums/SystemAlertEnums.cs).
- Catalog endpoint: `GET /api/alerting/system/catalog` in
  [`SystemAlertingController`](../../src/NodePilot.Api/Controllers/SystemAlertingController.cs).
- Guard test: [`SystemAlertCatalogTests`](../../tests/NodePilot.Engine.Tests/SystemAlerts/SystemAlertCatalogTests.cs).
- Supersedes the split-catalog approach of ADR 0007's alerting notes; related: [ADR 0001](0001-system-configuration-backup-restore.md)
  (backup format the alerting section extends), [docs/alerting.md](../alerting.md).
