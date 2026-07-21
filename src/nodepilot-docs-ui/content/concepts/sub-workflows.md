# Sub-Workflows & Contract

`startWorkflow` ruft einen Child-Workflow in einem **frischen DI-Scope** auf. Das ermöglicht Wiederverwendung und Komposition von Workflows.

## Modus

- `waitForCompletion: true` (default) → Parent blockiert, bis der Child fertig ist. Child-`returnData` wird als `param.*` in den Parent gespiegelt.
- `waitForCompletion: false` → fire-and-forget. Parent erhält `param.workflowId` / `param.workflowName` / `param.waited`.

**Max Call-Depth: 10.** `forEach` teilt sich das `ISubWorkflowGate` mit `startWorkflow`.

## Contract-Derivation

`GET /{id}/contract` liefert die Schnittstelle eines Workflows:

- **Inputs** aus `manualTrigger.parameters`.
- **Outputs** aus `returnData.data`-Keys + System-Outputs (`__executionId`, `__status`, `__workflowId`, `__workflowName`).

By-name-Lookup: **exakte Schreibweise gewinnt, sonst case-insensitive** — mehrdeutige Namen (Name ist nicht unique) liefern 409 statt eines stillen Zufallstreffers. Die Engine (`startWorkflow`/`forEach`) löst identisch auf, damit der Designer nie einen Contract zeigt, den die Runtime nicht findet.

## Feinheiten

- **`HasManualTrigger=false`** heißt **nicht** "nicht aufrufbar" — `startWorkflow` kann jeden enabled Workflow aufrufen. Es bedeutet nur: kein deklarierter Input-Contract; die UI fällt auf eine freie Parameter-Tabelle zurück.
- **Mehrere `returnData`-Nodes:** `HasMultipleReturnDataNodes=true`. Pro Lauf gewinnt nur einer (last-write-wins über das **ganze** JSON, nicht per Key). Outputs sind "may be available", nicht garantiert — die UI zeigt eine Warning.
- **Mehrere `manualTrigger`:** Parameter werden nach Namen dedupliziert. Bei divergierendem `type`/`default` gewinnt die erste Deklaration, `HasConflict=true` — UI-Warning, kein Hard-Fail. `Required` wird OR-aggregiert.
- **Reservierte Output-Keys** (`__executionId`, `__status`, `__workflowId`, `__workflowName`) werden aus User-`returnData.data` still gefiltert und separat von der Engine injiziert.
- **Disabled Nodes** (`manualTrigger` / `returnData` mit `data.disabled=true`) werden ignoriert — entspricht dem Engine-Skip-Verhalten.