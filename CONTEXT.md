# NodePilot Context

NodePilot is an agentless Windows Workflow Orchestrator. This glossary defines shared terms for architecture discussions and development. Use the linked documentation for implementation details; established UI labels and code identifiers retain their names.

## Language

**Workflow**:
A persisted automation definition made of nodes and edges that can be edited, published, triggered, and executed.
_Avoid_: Runbook, job

**Workflow Definition**:
The JSON document that stores a Workflow's nodes, edges, graph semantics, Activity Node configuration, and authoring metadata.
_Avoid_: React Flow JSON, canvas payload

**Activity (Activity Type)**:
A kind of executable workflow node, identified by `activityType`, such as `manualTrigger`, `runScript`, or `junction`. It defines how a node starts a Workflow, performs work, controls flow, or returns data.

**Activity Node**:
One configured instance of an Activity in a Workflow Definition, with its own node ID, label, configuration, and connections. Several nodes can use the same Activity Type. Sticky notes and visual groups are annotations, not Activity Nodes.

**Activity Catalog**:
The backend-owned catalog of static facts about built-in Activity Types, shared by runtime, telemetry, prompt checks, and designer affordances. Custom Activities obtain their metadata from their own definitions.
_Avoid_: Frontend activity list, activity constants

**Custom Activity**:
A reusable, user-authored Activity backed by a parameterized PowerShell template, with its own definition and versions. Nodes reference it through a `custom:<key>` Activity Type. See [Custom Activities](docs/custom-activities.md).

**Remote Activity**:
An Activity Type that supports execution on a Managed Machine over WinRM. Built-in types declare this capability through `IsRemote` in the Activity Catalog. The actual execution location depends on the configured target and credentials: supported local paths, including localhost without explicit credentials, execute on the NodePilot server instead of using WinRM.

**Local Execution**:
Activity work performed on the NodePilot server hosting the execution. Depending on the Activity and selected engine, it may run in-process or in a local child process. Local does not imply in-process.

**AI Agent Activity**:
An Activity that owns an iterative model/tool session with host-enforced permissions and budgets. Its optional tools can use WinRM; the Activity itself is not a Remote Activity.

**Agent Team**:
One AI Agent Activity Node containing exactly one supervisor and bounded parallel assignment batches. Members have separate sessions and share budgets, evidence and live peer pointers; they are never independent Workflow nodes. See [ADR 0017](docs/adr/0017-parallel-team-delegation.md).

**Agent Run**:
One bounded invocation of an AI Agent Activity, with a durable numbered event journal belonging to its Workflow Execution. See [ADR 0016](docs/adr/0016-general-ai-agent-activities.md).

**Managed Machine**:
A registered WinRM target machine that a Remote Activity can address.
_Avoid_: Host row, target record

**Trigger**:
An Activity Type whose nodes can act as Workflow entry points. Manual, schedule, and webhook triggers are specific kinds of Trigger; listeners are mechanisms that detect external events. See [ADR 0006](docs/adr/0006-trigger-only-workflow-roots.md).

**Junction**:
A control-flow Activity Type whose nodes explicitly join multiple incoming branches. Only Junction nodes
may have more than one incoming edge; their mode is `waitAll`, `waitAny`, or `waitNofM`. See [ADR 0013](docs/adr/0013-explicit-junction-fan-in.md).
_Avoid_: Implicit join, ordinary Activity with multiple inputs

**Workflow Execution**:
One persisted run of a Workflow, including its status, input parameters, step results, audit-relevant ownership, and parent-child lineage. It already exists while its status is `Pending`; "run" is an acceptable shorthand when the scope is clear.

**Step Execution**:
The execution record for one Activity Node within a Workflow Execution, including its status, output, errors, timing, and attempt count. It is distinct from the Activity Type and the configured node; a retry can update the same Step Execution record.

**Pending Execution**:
A Workflow Execution in the `Pending` state: accepted and persisted, but not yet taken over by the engine. Engine ownership changes the same execution to `Running`; it does not create a second execution. See [ADR 0014](docs/adr/0014-durable-execution-dispatch.md).
_Avoid_: Queue item only, fire-and-forget task

**Dispatch Intent**:
The description of how an execution enters engine ownership, including trigger source, parameters, lineage, priority, and admission policy. On admission, it is persisted in the dispatch outbox alongside the Pending Execution, with protected parameters. See [ADR 0014](docs/adr/0014-durable-execution-dispatch.md).
_Avoid_: Execute request, in-memory queue callback

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
A structured PowerShell-backed operation with a rendered script envelope, result markers, parsed output parameters, and timeout behaviour. Its execution can be local or over WinRM, depending on target resolution.
_Avoid_: Script snippet, command builder

**Workflow Contract**:
The static calling shape of a Workflow: manual inputs, return data outputs, and system outputs.
_Avoid_: Sub-workflow API

**Database Availability (Breaker)**:
The process-wide assessment of whether the application database is usable. It gates database-dependent requests during an outage and coordinates recovery; a single slow query does not establish an outage. State transitions, probes, and HTTP behaviour are defined in [ADR 0011](docs/adr/0011-database-availability-breaker.md).
_Avoid_: circuit breaker tripped/reset terminology in user-facing copy - the UI says "database unreachable / reconnecting".

## Relationships

- A **Workflow** has one current **Workflow Definition**; previous versions can be retained separately.
- A **Workflow Definition** contains **Activity Nodes**, edges, and optional annotations. Each Activity Node selects one **Activity Type**.
- A **Trigger** node can become a root of the runtime graph; only a **Junction** node may receive multiple incoming edges.
- The **Activity Catalog** describes built-in **Activity Types**. **Custom Activities** have separate definitions and versions.
- A **Remote Activity** supports a **Managed Machine** target over WinRM; supported local targets execute on the NodePilot server.
- Admission commits a **Pending Execution** and its protected **Dispatch Intent** atomically. Accepted pending work can survive restart until engine ownership.
- A **Maintenance Window** can block a **Dispatch Intent** from being admitted, but never stops an already-running **Workflow Execution**.
- Engine ownership changes a **Pending Execution** to `Running` on the same **Workflow Execution** record.
- A **Workflow Execution** has **Step Executions** that record the outcomes of its **Activity Nodes**.
- A **Settings Section** is owned by exactly one **Settings Section Adapter**.
- Selected **Activities** use **PowerShell Operations** for local or remote work.
- A **Workflow Contract** is derived from a **Workflow Definition**.

## Example Dialogue

> **Dev:** "When a **Trigger** fires, do we enqueue a callback directly?"
> **Domain expert:** "Admission first persists the **Workflow Execution** as `Pending` together with its **Dispatch Intent**, so failover and cancellation can see the accepted work. The engine later claims that same execution."

## Flagged Ambiguities

- "step" is context-dependent: use **Activity Type** for the kind of work, **Activity Node** for the configured graph node, **Step Execution** for its execution record, and **Workflow Execution** for the whole run.
- "definition" should mean **Workflow Definition**, not a React Flow implementation detail.
- "remote" capability in the catalog does not establish where a particular **Step Execution** ran; **Local Execution** does not establish which process ran it.
