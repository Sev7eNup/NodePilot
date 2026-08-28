# NodePilot — Activity & Definition Reference

Shared reference for a NodePilot workflow definition: the node/edge schema, the
activity catalog, variable substitution, the embedded-PowerShell rules, and the
layout style. Used both when generating a new workflow and when explaining or
editing an existing one.

## Node schema

```json
{
  "id": "step-1",
  "type": "activity",
  "position": { "x": 100, "y": 200 },
  "data": {
    "label": "Human-readable name",
    "activityType": "<one of the catalog below>",
    "outputVariable": "myVarName",
    "targetMachineId": "<guid or null>",
    "credentialId": "<guid or null>",
    "config": { /* activity-specific keys */ }
  }
}
```

`outputVariable` is optional (defaults to `id`); `config` is required and shape
depends on `activityType`. `targetMachineId` selects the WinRM target for remote
activities; `credentialId` references a stored credential. `position` is the
canvas coordinate — preserve it for nodes you are not moving. Two node types are
visual-only documentation, not executed: `type: "stickyNote"` and `type: "group"`
(group nodes are layout containers; child nodes reference them via `parentId`).

## Edge schema

```json
{
  "id": "e1",
  "source": "step-1",
  "target": "step-2",
  "type": "labeled",
  "sourceHandle": "out",
  "targetHandle": "in",
  "data": {
    "label": "On Success",
    "condition": "step-1.success"
  }
}
```

`condition` shortcuts: `"<sourceId>.success"`, `"<sourceId>.failed"`, or `null`
(always). For complex conditions use `conditionExpression` (an object with
operators `==`, `!=`, `<`, `>`, `contains`, `startsWith`, `endsWith`, `matches`,
`isEmpty`, `isNotEmpty`, `isTrue`, `isFalse`, plus `group` AND/OR and `not`) —
keep it simple, prefer the `condition` shortcut where possible. `sourceHandle` /
`targetHandle` pin which port an edge attaches to — preserve them on edges you are
not re-routing. `data.disabled: true` skips an edge.

## Fan-in topology

An ordinary Activity may have at most one incoming edge. Whenever two or more
branches converge, route every branch into an explicit `junction` Activity and
connect that Junction to the downstream Activity. Choose its `config.mode`
deliberately: `waitAll`, `waitAny`, or `waitNofM` (with `requiredCount`). Never
generate an implicit fan-in directly on a non-Junction Activity.

<!--ACTIVITY_CATALOG-->

## Variable substitution

Reference upstream values with `{{stepId.field}}`:

- `{{step-1.output}}` — stdout
- `{{step-1.error}}` — stderr
- `{{step-1.success}}` — `"true"` / `"false"`
- `{{step-1.param.hostname}}` — output param (e.g. a `$hostname` variable from runScript)
- `{{globals.NAME}}` — admin-managed global
- `{{manual.<name>}}` — a trigger-supplied value (also exposed as `param.*` of the trigger node)

Only these four tails (`output`, `error`, `success`, `param.X`) resolve — other
tails stay as literal text. In `runScript` scripts, do **not** wrap `{{var}}` in
quotes — the engine inserts already-quoted PowerShell strings.

## PowerShell inside `runScript` nodes — real, working code

When a workflow needs to do anything Windows-shaped, use a `runScript` node and
write actual PowerShell — no placeholders, TODOs, or pseudo-code.

1. PowerShell 5.1 / 7.x compatible. No third-party modules unless explicitly named.
2. No `Read-Host`, `Get-Credential`, or any interactive prompt — runs non-interactively over WinRM.
3. Use `Write-Error` + `exit 1` for failure paths; avoid `Write-Host` (it bypasses structured output capture).
4. Reference upstream values with bare `{{stepId.field}}` (no surrounding quotes).
5. Declare `$variableName = ...` at script scope to expose values downstream as `{{thisStepId.param.variableName}}`.
6. Embed the script as a JSON string with `\n` for line breaks. Keep each step focused.

## Layout style

- **Left-to-right flow.** Trigger node at `x: 0`, every successor at `x += 300`
  (normal step) or `x += 340..400` (when fanning out to ≥5 branches).
- **Main lane y constant.** Pick a base y (e.g. `200`) for the main path.
- **Branch lanes** offset by `±180` per branch.
- Position values are integers (no fractional pixels). When adding a node to an
  existing workflow, place it sensibly relative to its neighbours and leave all
  other nodes' positions untouched.

## Variable / parameter conventions

- For `manualTrigger` parameters: `type` is always the string literal `"string"`
  even if the value is conceptually a number — the UI binds it as a typed input.
- Parameter `default` values must be strings (or null), not numbers/booleans.
