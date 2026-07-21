# ADR 0006 - Trigger-Only Workflow Roots

**Status:** Implemented - 2026-05-17
**Scope:** Runtime graph entry-point semantics.

## Kontext

A workflow graph can contain activities with no incoming edges. Treating every in-degree-zero node
as an implicit root makes authoring mistakes dangerous: disconnected work could run merely because a
node was left orphaned on the canvas. NodePilot's domain language already separates **Trigger** from
ordinary **Activity**; runtime graph roots should follow that distinction.

## Entscheidung

Execution roots are **trigger-only**:

- Active trigger activities can become roots.
- Ordinary activities with no incoming edge are not roots.
- Disabled triggers are not roots.
- A workflow with zero active trigger roots fails instead of falling back to in-degree-zero
  activities.
- Orphan non-trigger activities remain unreachable and are skipped.

This applies to all trigger types, not only `manualTrigger`: schedule, webhook, file watcher,
database, and event-log triggers are valid roots when enabled.

## Konsequenzen

- Workflow execution is event-driven and explicit.
- Canvas lint can warn about orphan roots and unreachable nodes without changing runtime semantics.
- Tests must build runnable fixtures with a trigger node, even when the trigger itself is mocked.
- Sub-workflows remain callable through dispatch intent; their contract may exist even without a
  `manualTrigger`, but declared inputs come from manual-trigger parameters when present.

## Referenzen

- [../../CONTEXT.md](../../CONTEXT.md)
- [../workflow-designer-features.md](../workflow-designer-features.md)
- [../../tests/NodePilot.Engine.Tests/WorkflowEngineTriggerRootTests.cs](../../tests/NodePilot.Engine.Tests/WorkflowEngineTriggerRootTests.cs)
