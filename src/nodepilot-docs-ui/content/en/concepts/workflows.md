# Workflows & activities

A workflow is a directed process. Nodes represent triggers and activities. Edges connect nodes and can carry conditions. The engine starts at the trigger and then activates every reachable path.

## Structure

- **Trigger nodes** are the roots of a run (`manualTrigger`, `scheduleTrigger`, …) and inject event data as `{{manual.*}}` variables.
- **Activity nodes** are the work steps. Every activity has an `activityType`, optionally a `targetMachineId` (remote) and a `config` object.
- **Edges** connect nodes and carry **conditions** that decide whether the target node is executed.

## Activity scopes

| Scope | Execution |
|---|---|
| **Remote** | On the target machine via `targetMachineId` / WinRM |
| **Engine-local** | In the API process |
| **Hybrid** | Both (`runScript`, `waitForCondition`) |
| **ControlFlow** | Engine-local, category `ControlFlow` in the `ActivityCatalog` (a palette axis, independent of the scope) |

The full list of all 27 activity types with their configuration keys and output semantics: [Activity reference](../activities-reference).

## Execution lifecycle

For every step, a workflow run (`POST /execute`, asynchronous, `202` + `ExecutionId`) goes through:

1. Resolving the templates in `config` against the data bus.
2. Executing the activity in the per-step DI scope.
3. Writing the outputs (`output`, `error`, `success`, `param.*`) onto the data bus.
4. Evaluating the outgoing edge conditions → scheduling the target nodes.

Step states: `Pending`, `Running`, `Succeeded`, `Failed`, `Skipped`, `Paused`.

## Triggers after a restart or failover

**Nothing is caught up.** Every trigger source keeps a durable cursor, but that cursor exists for
deduplication and diagnostics — not for backfilling. On start each source fast-forwards it to the
current state without firing, and writes one log line plus a
`nodepilot.scheduler.triggers.fires_skipped` counter for the size of the window it skipped. Without
this rule a per-minute schedule produces 60 runs per hour of downtime, per workflow.

The **running** service is unaffected: a signal a live source has already observed is retried until
the database accepts it, and the file watcher and event-log sources still recover notifications that
escape them while they are up.

The price is stated plainly: files created and event-log entries written while NodePilot was
stopped, failing over, or without a leader are not processed. If a workload cannot lose those, put a
durable queue in front of it rather than relying on the trigger.

## Retry & timeout

- **Retry per step:** `config.retry` with `maxAttempts`, `backoff`, `initialDelayMs`, `maxDelayMs`.
- **Execution timeout:** `timeoutSeconds` in the execute body + per-step `config.timeoutSeconds`.

## Disabled nodes & edges

- `data.disabled: true` → the node becomes `Skipped`; downstream nodes without another source do too.
- `disabled: true` on an edge → the target node does not become a root.
- **No (active) trigger** (trigger-less **or** only cycles) → 0 roots → `Failed` with an error message and a warning. Roots are trigger nodes exclusively (there is no `inDegree==0` fallback).
- **An empty workflow** (0 nodes) → runs through with 0 steps (`Succeeded`).

## Version history & edit lock

`Update` / `Rollback` snapshot the previous definition. A per-user edit lock (`CheckedOutByUserId` + `CheckedOutAt`) protects against concurrent edits — mutating endpoints return `423 Locked` if the caller is not the lock owner. Details: [Workflow control flow](../api/workflow-control).
