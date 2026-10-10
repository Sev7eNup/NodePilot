# ADR 0018 - Active sub-workflow capacity

**Status:** Accepted — 2026-10-09
**Scope:** Shared capacity used by synchronous `startWorkflow` and `forEach` executions.

## Kontext

A child Workflow Execution can synchronously await another child. Holding every ancestor's
sub-workflow slot until completion allows an acyclic call chain to exhaust the same semaphore
needed by its descendant. At capacity one, `A -> B -> C` cannot progress. At the default
capacity, unrelated parents can reproduce the same starvation under load.

Reentrant per-call-tree admission would let parallel descendants bypass the cap. Releasing a
parent's slot whenever any one branch waits would also undercount its other running branches.
Neither approach represents the work the gate is intended to bound.

## Entscheidung

Count child Workflows with active work, excluding ancestors whose participating steps are all
suspended awaiting descendants. The child-invocation module owns a shared lease; the Workflow
Scheduler tracks participation separately for each step. A waiting step suspends only its own
participation. A runnable sibling keeps the Workflow's slot occupied. Resumption must acquire
capacity before that step continues, and concurrent resumptions share one Workflow slot.

Use the same module for `startWorkflow` and `forEach`. Cancellation and completion release each
participation and physical semaphore slot exactly once. Per-Workflow admission is obtained
before global sub-workflow capacity, so callers waiting on one Workflow cannot monopolize the
global pool needed by its descendants.

The separate Engine global execution cap, per-Workflow execution limits, call-depth limit,
per-loop parallelism and step concurrency limits remain enforced. The sub-workflow cap is not
an exemption for an entire descendant tree.

## Konsequenzen

Healthy nested calls can make progress under pressure without unbounded admission. The
sub-workflow cap no longer includes fully suspended ancestors; the unchanged Engine global
cap still bounds total running executions, including those ancestors. A configured
per-Workflow limit can still prevent recursive calls into that same Workflow until timeout;
this decision does not weaken that explicit limit.

Participation is per Workflow step, not per internal agent-team member. The existing
[Agent Team wait trade-off](0017-parallel-team-delegation.md#concurrency-invariants) remains:
other members may continue during a serialized `workflow_run` wait while that step lends
its slots. Team budgets and Engine limits still apply.

Scheduler and invocation code must share the lease's participation protocol. Tests must cover
parallel siblings, last-participant suspension, resumption, cancellation and gate contention
in addition to flat and nested invocation cases.

## Referenzen

- [Workflow Scheduler](../../src/NodePilot.Engine/Execution/WorkflowScheduler.cs)
- [Shared lease](../../src/NodePilot.Engine/Execution/SubWorkflowGateLease.cs)
- [Participation regressions](../../tests/NodePilot.Engine.Tests/Execution/SubWorkflowGateLeaseTests.cs)
- [startWorkflow](../../src/NodePilot.Engine/Activities/StartWorkflowActivity.cs)
- [forEach](../../src/NodePilot.Engine/Activities/ForEachActivity.cs)
- [Performance and capacity](../performance-improvements.md)
- [Review evidence](../architecture-security-review-2026-10-09.md)
