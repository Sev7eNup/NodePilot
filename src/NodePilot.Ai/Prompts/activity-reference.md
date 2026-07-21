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

## Activity catalog (use only these `activityType` values)

**Triggers**
- `manualTrigger` — entry point, optional `parameters: [{name, type:"string", required, default}]`
- `scheduleTrigger` — `cronExpression` (7-field Quartz cron)
- `webhookTrigger` — `path`, `method`, optional `secret`
- `fileWatcherTrigger` — `directory`, `filter`, `watchType`, `includeSubdirectories`
- `databaseTrigger` — `connectionRef`, `provider`, `query`, `intervalSeconds`; raw `connectionString` is dev/legacy only and blocked in hardened deployments
- `eventLogTrigger` — `logName`, `source`, `entryType`, `messagePattern`

**Run Script (local by default, remote when targeted)**
- `runScript` — `script` (PowerShell), `engine`, `timeoutSeconds`, and (local execution only) `isolated` (boolean — run in a separate process inside a Windows Job Object for crash/leak containment and no orphaned processes), with optional caps `memoryLimitMb` and `maxProcesses`. **Step success is error-based:** only a terminating PowerShell error (`throw`, or `Write-Error` under the wrapper's `Stop` preference) fails the step — an explicit `exit N` does NOT fail it. To make a non-zero exit fail the step, set `successExitCodes` (comma-separated, e.g. `"0"` or `"0,1"`). The exit code is always exposed as `{{step.param.exitCode}}`. Omit `targetMachineId` when the script should run on the NodePilot host. Set a non-local `targetMachineId` when NodePilot should execute the script through its WinRM wrapper on that target. A local runScript may still use PowerShell remoting itself (`Invoke-Command`, `New-PSSession`, etc.).

**Remote (WinRM, requires `targetMachineId`)**
- `fileOperation` — `operation` (copy/move/delete/exists/create/rename), `path` (file path), `destination` (copy/move), `newName` (rename). Operates on files only — destructive ops assert `-PathType Leaf` so a folder typed by mistake fails fast. `create` makes an empty file (truncates with `-Force` if it exists; refuses if a folder occupies the path).
- `folderOperation` — `operation` (copy/move/delete/exists/list/create/rename), `path` (folder path), `destination` (copy/move), `newName` (rename). Operates on folders only — destructive ops assert `-PathType Container`. Use `create` to make a new directory, `list` to enumerate immediate children.
- `serviceManagement` — `serviceName`, `action` (start/stop/restart/status/create/delete/setStartType). For `create`: `binaryPath` (required), `displayName`, `description`, `startupType`. For `setStartType`: `startupType` (Automatic/AutomaticDelayedStart/Manual/Disabled).
- `registryOperation` — `operation` (one of `read`/`write`/`deleteValue`/`deleteKey`/`createKey`/`exists`/`listSubKeys`/`listValues`), `keyPath`, `valueName` (required for `write`/`deleteValue`; optional for `read`/`exists`), `value` (write), `valueType` (`String`/`ExpandString`/`Binary`/`DWord`/`MultiString`/`QWord` — write only, default `String`). Outputs depending on op: single `read`/`exists` → `param.value`+`param.type` / `param.exists`; `read` without valueName / `listValues` → `param.values`+`param.count`; `listSubKeys` → `param.subKeys`+`param.count`; `createKey` → `param.created`. `deleteKey` removes the key recursively.
- `wmiQuery` — `mode` (`query` | `wql` | `invokeMethod`, default `query`), `namespace`. Per mode: `query` → `className` + optional `filter` (WHERE clause); `wql` → `query` (raw `SELECT … FROM …`); `invokeMethod` → `className`, `methodName`, optional `arguments` (JSON object → PS hashtable; keys must be valid identifiers), optional `filter` (scopes to instance methods, otherwise treated as a static call).
- `startProgram` — `filePath`, `arguments`, `waitForExit`, `timeoutSeconds`
- `powerManagement` — `action` (shutdown/restart/logoff/abort/hibernate), `delaySeconds`, `force`
- `textFileEdit` — `operation` (`append`/`prepend`/`insert`/`replaceLine`/`delete`/`replace`), `path` (file path). Per-op keys: `content` (append/prepend/insert/replaceLine — multi-line via `\n`), `lineNumber` (insert/replaceLine, 1-based), `lineRange: [from, to]` OR `matchPattern` OR `lineNumber` for `delete` (exactly one), `matchPattern`+`replace` for `replace` (plus optional `useRegex`, `ignoreCase`, `occurrences`: `all`/`first`). Common: `encoding` (`auto`/`utf8`/`utf8-bom`/`utf16le`/`utf16be`/`ascii`), `lineEnding` (`preserve`/`crlf`/`lf`), `createIfMissing` (append/prepend only), `backupSuffix` (e.g. `.bak`), `dryRun`, `maxFileSizeMB`. Append also supports `appendIfMissing` for idempotent edits (e.g. /etc/hosts entries).

**Engine-local**
- `restApi` — `url`, `method`, `body`, `headers`, `timeoutSeconds`
- `sql` — `provider` (sqlserver/sqlite/postgres), `query`. Connection: use named `connectionRef` for DB credentials in hardened deployments. Builder fields without credentials are allowed for integrated SQL Server auth or SQLite file paths; raw `connectionString` and builder credentials are dev/legacy only and may be blocked.
- `emailNotification` — `to`, `subject`, `body`, `isHtml`
- `delay` — `seconds`
- `generateText` — random string for ids/tokens/guids/password charsets. `mode` (alphanumeric/alphabetic/numeric/hex/guid/password/custom), `length`, `customCharset` (mode=custom), `excludeAmbiguous`. Note: `password` is only a charset (letters+digits+symbols); it does NOT guarantee password-policy complexity.
- `junction` — `mode` (waitAll/waitAny/waitNofM), `requiredCount` (note: NOT `n`)
- `startWorkflow` — `workflowNameOrId`, `parameters`, `waitForCompletion`
- `returnData` — `data` (object with `{{template}}` values)
- `xmlQuery` — `source`, `path`/`content`, `xpath`, `namespaces`
- `jsonQuery` — `source`, `path`/`content`, `jsonPath`
- `log` — `level` (info/warning/error), `message`

Some workflows also contain control-flow / niche activity types not listed above
(for example loop, decision, wait-for-condition, file-hash, zip and
scheduled-task activities). When such a node is present, its concrete metadata is
supplied separately in the per-node context block — explain it from that plus its
`config`.

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
