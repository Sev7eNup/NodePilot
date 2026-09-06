# ADR 0015 - Fail-Closed Edge Conditions

**Status:** Implemented — 2026-09-06
**Scope:** Edge condition evaluation, definition validation at Save/Publish/Import, scheduler failure semantics

## Kontext

An edge condition guards the path behind it. The evaluator answered `true` for everything it could
not interpret: an unknown `type`, a group with an unknown operator or without a `children` array,
a legacy shortcut that was not `<step>.success|failed`, a reference to a step that had no result.
A comparison whose variable never arrived resolved to the empty string, so `!=`, `isEmpty` and
`contains ""` held for a missing value, and `not` turned every safe answer into an open one.

Nothing validated a condition before it was stored. The structural validator checked edge ids and
endpoints only, the canvas linter and the analyzer had no condition rule, and the alerting filter
validator covered alerting fields only. A condition mangled by an import, an MCP edit, an AI
proposal or a renamed step therefore opened the branch it was written to protect, and the run
reported success.

## Entscheidung

- A condition that cannot be evaluated is rejected where the definition is written:
  `EdgeConditionValidator` in Core runs inside `WorkflowDefinitionStructuralValidator`, so Save,
  Publish, Rollback, native import, SCOrch import, MCP and AI merge all answer `400` with code
  `invalid-edge-condition` and the offending JSON path. Accepted are the node types `group`,
  `not` and `comparison`; group operators `AND`/`OR`; the comparison operators the evaluator
  implements; operands of kind `literal` or `variable` with source `step`, `global` or `manual`;
  and step references that name a node id or an output-variable alias of the same workflow. The
  legacy shortcut must be `<step or alias>.success|failed`.
- At run time the evaluator throws `ConditionEvaluationException` for such a condition. The
  scheduler fails the run and names the edge (`Edge 'e1' (a -> b) has an invalid condition: …`).
  The edge is neither opened nor silently skipped.
- Evaluation is three-valued. A variable operand without a value in this run — a step without a
  result, a missing output parameter, a global or trigger input that does not exist — makes its
  comparison undecidable. Undecidable never satisfies a condition, `not` keeps it undecidable, and
  inside a group only a decided `true` or `false` counts: one `false` settles `AND`, one `true`
  settles `OR`. A legacy shortcut on a step without a result is `false`.
- Alerting filters keep their semantics: an `event` field absent from an observation reads as
  empty, because the field catalog is declared and validated separately. A malformed alerting
  filter matches nothing.

## Konsequenzen

A guard guards. A workflow with a broken condition can no longer be saved or published; an
already stored one fails its next run with an actionable message instead of taking the branch.
Authors who relied on `!=` or `isEmpty` against a value that may not exist must express that
explicitly, because a missing value no longer counts as "not equal" or "empty". Decision-node
cases use the same evaluator; a case with an unknown type now fails the step with the case name
rather than matching. The designer's condition builder already emits only accepted shapes, so
the change is visible to hand-written, imported and AI-generated definitions.
