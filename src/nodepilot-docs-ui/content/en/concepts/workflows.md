# Workflows & activities

A workflow is a directed process. Nodes represent triggers and activities. Edges connect nodes and can carry conditions. The engine starts at the trigger and then activates every reachable path.

## Structure

- **Trigger nodes** are the roots of a run (`manualTrigger`, `scheduleTrigger`, …) and inject event data as `{{manual.*}}` variables.
- **Activity nodes** are the work steps. Every activity has an `activityType`, optionally a `targetMachineId` (remote) and a `config` object.
- **Edges** connect nodes and carry **conditions** that decide whether the target node is executed.

Conditions fail closed. A condition that cannot be evaluated — unknown type or operator, missing operand, a shortcut other than `<step>.success|failed`, a reference to a step that does not exist — is rejected when the workflow is saved or published, and a run that still meets one fails and names the edge. A comparison whose variable has no value in this run (a step without a result, a missing output parameter, a global or trigger input that does not exist) never holds, not even behind `not`.

## Portable import and export

Each exported workflow carries `sourceId` and `dependencies` describing static
machine, credential, skill, MCP server and workflow references, including agent
team members. Import assigns destination IDs: machines match by name and WinRM
target, credentials by name and account, skills by name/version/SHA-256, MCP
servers by name and transport, and workflows by unique name. Workflows imported
together refer to the newly created copies.

Missing or ambiguous dependencies are reported and replaced with non-executable
ID placeholders. Publishing and enabling are blocked until these are corrected
in the designer. Unresolved skills can be removed and selected again.
Alternatively, set a dependency's `targetId` to an available destination ID before
importing. Workflow access permissions still apply.

Every imported workflow starts disabled. Passwords, MCP credentials, tool approvals
and skill archive files are not transferred. Register servers and install skill
packages on the destination first. Existing secret redaction, including instructions
and scripts, remains in effect. Legacy exports without dependency metadata require
manual reassignment of static IDs. Dynamic expressions, paths and workflow calls
using names remain unchanged and must suit the destination environment.

## Activity scopes

| Scope | Execution |
|---|---|
| **Remote** | On the target machine via `targetMachineId` / WinRM |
| **Engine-local** | In the API process |
| **Hybrid** | Both (`runScript`, `waitForCondition`) |
| **ControlFlow** | Engine-local, category `ControlFlow` in the `ActivityCatalog` (a palette axis, independent of the scope) |

The full list of all 29 activity types with their configuration keys and output semantics: [Activity reference](../activities-reference).

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

- **Retry per step:** `config.retry` with `maxAttempts`, `backoff`, `initialDelayMs`, `maxDelayMs`. Permanent remote failures are not retried — a denied WinRM logon, a session blocked by the SSL policy and a credential that cannot be decrypted fail the step on the first attempt, so a step never produces a series of failed logons that could lock an account out.
- **Execution timeout:** `timeoutSeconds` in the execute body + per-step `config.timeoutSeconds`.

## Disabled nodes & edges

- `data.disabled: true` → the node becomes `Skipped`; downstream nodes without another source do too.
- `disabled: true` on an edge → the target node does not become a root.
- **No (active) trigger** (trigger-less **or** only cycles) → 0 roots → `Failed` with an error message and a warning. Roots are trigger nodes exclusively (there is no `inDegree==0` fallback).
- **An empty workflow** (0 nodes) → runs through with 0 steps (`Succeeded`).

## Version history & edit lock

`Update` / `Rollback` snapshot the previous definition. A per-user edit lock (`CheckedOutByUserId` + `CheckedOutAt`) protects against concurrent edits — mutating endpoints return `423 Locked` if the caller is not the lock owner. Details: [Workflow control flow](../api/workflow-control).
