# NodePilot Context

NodePilot is an agentless Windows Workflow Orchestrator. This context captures the project language used for workflow authoring, execution, settings, and remote operation architecture.

## Language

**Workflow**:
A persisted automation definition made of nodes and edges that can be edited, published, triggered, and executed.
_Avoid_: Runbook, job

**Workflow Definition**:
The JSON document that stores a Workflow's nodes, edges, graph semantics, Activity configuration, and authoring metadata.
_Avoid_: React Flow JSON, canvas payload

**Activity**:
A workflow node type that either starts a Workflow, performs work, controls flow, or returns data.
_Avoid_: Step type, task

**Activity Catalog**:
The backend-owned catalog of static Activity facts shared by runtime, telemetry, prompt checks, and designer affordances.
_Avoid_: Frontend activity list, activity constants

**Remote Activity**:
An Activity that executes work on a Managed Machine over WinRM. Its remote-ness is the `IsRemote` flag in the Activity Catalog.
_Avoid_: Remote step

**Managed Machine**:
A registered WinRM target machine that a Remote Activity can address.
_Avoid_: Host row, target record

**Trigger**:
An Activity that can act as a Workflow entry point.
_Avoid_: Schedule, listener, webhook

**Workflow Execution**:
One run of a Workflow, including its status, input parameters, step results, audit-relevant ownership, and parent-child lineage.
_Avoid_: Run, job instance

**Pending Execution**:
A persisted Workflow Execution row that has been accepted but not yet taken over by the engine.
_Avoid_: Queue item only, fire-and-forget task

**Dispatch Intent**:
The caller's request to create and enqueue a Pending Execution with trigger source, parameters, ownership, priority, and enabled-check policy.
_Avoid_: Execute request, queue callback

**Maintenance Window**:
A scheduled period that gates which Workflows may be *newly admitted* to run. It is admission control, not a kill-switch: it never cancels in-flight runs and never re-gates a resume/retry. Modes are Blackout (deny) and AllowOnly (permit only the listed scope); when windows overlap, deny wins.
_Avoid_: Freeze, downtime flag, kill-switch

**Settings Section**:
One operator-editable configuration root exposed through the Admin Settings surface.
_Avoid_: Settings card, config form

**Settings Section Adapter**:
The backend module that owns one Settings Section's DTO, defaults, secret handling, config keys, validation, and JSON persistence shape.
_Avoid_: Controller switch case

**PowerShell Operation**:
A structured PowerShell-backed remote operation with a rendered script envelope, result markers, parsed output parameters, and timeout behaviour.
_Avoid_: Script snippet, command builder

**Workflow Contract**:
The static calling shape of a Workflow: manual inputs, return data outputs, and system outputs.
_Avoid_: Sub-workflow API

## Relationships

- A **Workflow** contains exactly one **Workflow Definition**.
- A **Workflow Definition** contains zero or more **Activities** and edges.
- A **Trigger** is an **Activity** that can become a root of the runtime graph.
- An **Activity Catalog** describes static facts about every executable **Activity**, including whether it is a **Remote Activity** (the `IsRemote` flag).
- A **Remote Activity** executes against one **Managed Machine** over WinRM.
- A **Dispatch Intent** creates one **Pending Execution**.
- A **Maintenance Window** can block a **Dispatch Intent** from being admitted, but never stops an already-running **Workflow Execution**.
- A **Pending Execution** becomes one **Workflow Execution** when the engine takes ownership.
- A **Settings Section** is owned by exactly one **Settings Section Adapter**.
- A **PowerShell Operation** is used by selected remote **Activities**.
- A **Workflow Contract** is derived from a **Workflow Definition**.

## Example Dialogue

> **Dev:** "When a **Trigger** fires, do we enqueue a callback directly?"
> **Domain expert:** "No. A **Dispatch Intent** creates a **Pending Execution** first, so failover and cancellation can see the accepted work."

## Flagged Ambiguities

- "step" is used in UI text and database rows, but architecture discussions should say **Activity** for the node type and **Workflow Execution** for the run-level record.
- "definition" should mean **Workflow Definition**, not a React Flow implementation detail.
