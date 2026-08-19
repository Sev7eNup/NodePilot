# Sub-workflows & contract

The `startWorkflow` activity starts another workflow. The calling workflow is the parent, the started workflow the child. The child gets its own dependency-injection scope.

## Mode

- `waitForCompletion: true` (default) → the parent waits for the child. The child's `returnData` is mirrored into the parent as `param.*`.
- `waitForCompletion: false` → fire and forget. The parent receives `param.workflowId` / `param.workflowName` / `param.waited`.

**Maximum call depth: 10.** `forEach` shares the `ISubWorkflowGate` with `startWorkflow`.

## Contract derivation

`GET /{id}/contract` returns a workflow's interface:

- **Inputs** from `manualTrigger.parameters`.
- **Outputs** from the `returnData.data` keys + system outputs (`__executionId`, `__status`, `__workflowId`, `__workflowName`).

By-name lookup: **an exact-case match wins, otherwise case-insensitive** — ambiguous names (names are not unique) return 409 rather than a silent random hit. The engine (`startWorkflow`/`forEach`) resolves identically, so the designer never shows a contract the runtime cannot find.

## Subtleties

- **`HasManualTrigger=false`** does **not** mean "cannot be called" — `startWorkflow` can call any enabled workflow. It only means there is no declared input contract; the UI falls back to a free-form parameter table.
- **Several `returnData` nodes:** `HasMultipleReturnDataNodes=true`. Only one wins per run (last write wins over the **whole** JSON, not per key). Outputs are "may be available", not guaranteed — the UI shows a warning.
- **Several `manualTrigger` nodes:** parameters are deduplicated by name. If `type`/`default` diverge, the first declaration wins and `HasConflict=true` — a UI warning, not a hard failure. `Required` is aggregated with OR.
- **Reserved output keys** (`__executionId`, `__status`, `__workflowId`, `__workflowName`) are silently filtered out of a user's `returnData.data` and injected separately by the engine.
- **Disabled nodes** (`manualTrigger` / `returnData` with `data.disabled=true`) are ignored — matching the engine's skip behaviour.
