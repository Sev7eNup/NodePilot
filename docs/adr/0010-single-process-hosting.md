# ADR 0010 - Single-Process Hosting Topology

**Status:** Accepted - 2026-07-21
**Scope:** Process/deployment topology — one Windows Service hosts the API, engine, scheduler, and all background workers.

## Kontext

NodePilot has grown large in feature *breadth* — many activity types, controllers, a CLI, an
MCP server, a React UI, and a docs site. A natural question at this size is whether the single
Windows Service should be split into several services (for example API + worker + scheduler).
No stability, throughput, or security-isolation pressure has been observed that would force
such a split; the question is one of architectural cleanliness. This ADR records the deliberate
choice to stay single-process, so a future contributor does not reverse-engineer the topology
or undo it by accident.

## Entscheidung

NodePilot is hosted as **one ASP.NET Core process = one Windows Service**
(`builder.Host.UseWindowsService()`). The HTTP API, the SignalR hub, the Quartz scheduler +
`TriggerOrchestrator`, the in-process dispatch queue + worker pool, the `WorkflowEngine`, all
retention/alerting sweepers, and the HA leader lease share that one process and DI container.
We deliberately do **not** split into separate API / worker / scheduler services.

Why the alternatives lose:

- **Lean-replacement identity.** A multi-role deployment is exactly the System Center
  Orchestrator complexity (management server + N runbook servers + web service + multiple DBs)
  that NodePilot exists to avoid. One box, one service is a product differentiator.
- **Modularity is already solved at the assembly level.** The directed dependency graph
  (`Core ← Ai/Data/Remote/Telemetry ← Engine/Scheduler ← Api`) gives the sought-after
  separation without a process boundary. A process split adds operational cost (version-skew
  management, partial deploys, a durable broker) without adding modularity that is not already
  present.
- **The couplings a split must break are fast in-process statics.** Cancellation
  (`_runningExecutions`, Guid→`CancellationTokenSource`), the step-debugger (`_debugHandles`),
  and the concurrency caps (`_reservedExecutionSlots`/`_userExecutionCounts`) in
  `WorkflowEngine` are direct in-memory accesses, and the trigger→execution handoff is an
  in-process `ConcurrentQueue`. Moving the engine out of process would require externalizing all
  of these (DB cancel-flags or a message bus, centralized caps) and would degrade the
  interactive low-latency UX (instant cancel, live SignalR, breakpoints) for no current benefit.

## Konsequenzen

- The single Windows Service remains both the unit of deploy and the unit of HA failover.
- The usual split motives are already covered without a service boundary: **crash/ownership
  isolation** via active/passive HA (ADR 0002), and **runaway-script containment** at the step
  level via `runScript config.isolated:true` (own process in a Windows Job Object) plus the
  `ProcessExecutionEngine` path. Blast-radius isolation is fine-grained, not service-grained.
- Revisit this decision only when **measured**, not speculatively:
  1. heavy execution load demonstrably degrades API/SignalR responsiveness despite
     `isolated:true`, or
  2. a real need for **active/active** execution scaling beyond today's active/passive model, or
  3. a compliance requirement to run the WinRM/PowerShell tier under a separate identity or
     network segment from the internet-facing API.
  A future split would take the shape *API service + worker service* and reuse the existing
  durable `Pending` `WorkflowExecution` row (already stamped with `OwnerNodeId`) as the
  API→worker handoff — the in-memory queue is only an optimization on top of that row.
- This ADR documents an **existing** state; it introduces no new runtime invariant and no guard
  test to enforce.

## Referenzen

- [../../src/NodePilot.Api/Program.cs](../../src/NodePilot.Api/Program.cs) — `UseWindowsService()` composition root
- [../../src/NodePilot.Engine/WorkflowEngine.cs](../../src/NodePilot.Engine/WorkflowEngine.cs) — in-process `static` cancellation/debug/capacity state
- [../../src/NodePilot.Api/ExecutionDispatch/ExecutionDispatchService.cs](../../src/NodePilot.Api/ExecutionDispatch/ExecutionDispatchService.cs) — DB `Pending` row + in-process dispatch queue
- [0002-active-passive-ha.md](0002-active-passive-ha.md) — the HA model that supplies crash/ownership isolation
- [0004-secret-protector-providers.md](0004-secret-protector-providers.md) — cluster-portable secrets that HA depends on
- [../../deploy/README.md](../../deploy/README.md) — single-service install story
