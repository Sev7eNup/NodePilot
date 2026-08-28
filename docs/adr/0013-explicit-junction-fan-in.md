# ADR 0013 - Explicit Junction Fan-In

**Status:** Implemented — 2026-08-26
**Scope:** Workflow graph topology, authoring, import, validation, and scheduler join semantics

## Kontext

A Workflow Definition previously allowed several edges to target any Activity. The scheduler waited
for predecessors but evaluated only the edge associated with the predecessor that happened to
complete last. Conditional convergence was therefore timing-dependent, and an ordinary Activity
with several inputs did not state whether it meant wait-all, wait-any, or N-of-M.

NodePilot already has a `junction` control-flow Activity whose modes express those choices. Keeping
a second implicit join mechanism would leave two competing representations and make authored,
imported, AI-generated, and runtime graphs disagree.

## Entscheidung

- A non-Junction Activity may have at most one incoming edge.
- A `junction` is the only Activity allowed to have multiple incoming edges.
- Every convergence uses an explicit Junction with `waitAll`, `waitAny`, or `waitNofM` plus
  `requiredCount`.
- The designer offers to insert a `waitAll` Junction when an author creates a second input. It
  rewires the existing edge or edges through the Junction and selects it for mode configuration.
- Structural validation rejects direct fan-in at Save, Publish, API, MCP, and native JSON import
  boundaries. The canvas linter reports the same dedicated finding.
- SCOrch import inserts a `waitAll` Junction for imported direct fan-in and reports the conversion.
- The scheduler evaluates the complete relevant incoming-edge set. `waitAll` resolves after all
  inputs are completed or skipped and requires every completed input condition to match. `waitAny`
  and `waitNofM` count successful completed inputs whose edge conditions match, and skip only when
  the configured threshold can no longer be reached.

## Konsequenzen

The join policy is visible in the graph and no longer depends on predecessor completion order.
Designer, AI, imports, validation, and execution share one topology rule. Existing definitions with
direct fan-in must be repaired by inserting Junctions before they can be saved or published; SCOrch
imports are repaired automatically. A Junction adds one node and edge to each convergence and the
author must choose a mode when `waitAll` is not intended.

## Referenzen

- [WorkflowDefinitionStructuralValidator.cs](../../src/NodePilot.Core/WorkflowDefinitions/WorkflowDefinitionStructuralValidator.cs)
- [WorkflowScheduler.cs](../../src/NodePilot.Engine/Execution/WorkflowScheduler.cs)
- [ScorchImporter.cs](../../src/NodePilot.Engine/Scorch/ScorchImporter.cs)
- [junctionFanIn.ts](../../src/nodepilot-ui/src/lib/junctionFanIn.ts)
- [workflowLint.ts](../../src/nodepilot-ui/src/lib/workflowLint.ts)
- [WorkflowDefinitionStructuralValidatorTests.cs](../../tests/NodePilot.Engine.Tests/WorkflowDefinitions/WorkflowDefinitionStructuralValidatorTests.cs)
- [WorkflowSchedulerTests.cs](../../tests/NodePilot.Engine.Tests/Execution/WorkflowSchedulerTests.cs)
