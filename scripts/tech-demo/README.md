# NodePilot Tech-Demo — Full Showcase

A sample workflow that demonstrates every activity, every edge condition operator and every control-flow variant that NodePilot supports.

## Files

| File | Purpose |
|---|---|
| `main.json` | Main workflow (37 nodes, ~55 edges) |
| `child.json` | Small sub-workflow (3 nodes) that the main workflow calls via `startWorkflow` |
| `seed.ps1` | Seed script — signs in, creates and publishes the demo workflows |

`seed.ps1` reads the definitions, creates the workflows via `POST /api/workflows`
and then publishes them via `POST /api/workflows/{id}/publish`.

## Usage

The backend must be running (default `http://localhost:5000`).

```powershell
# From the repo root:
./scripts/tech-demo/seed.ps1

# Or with explicit parameters:
./scripts/tech-demo/seed.ps1 -BaseUrl http://localhost:5000 -AdminUser admin -AdminPassword 'Abcd1234!' -Force
```

The script detects automatically whether NodePilot is in bootstrap mode (empty user table + `admin-setup.token` present) and then uses the `X-Setup-Token` header for the first-admin login. Otherwise it prompts for the password as usual.

If workflows with the same name already exist, it asks: `k` = keep, `r` = delete + recreate, `a` = abort. With `-Force` it always replaces. Replacing also requires your own edit lock; foreign locks are not released. Ambiguous names abort the run.

## What the workflow shows

### Activities (all 18)

`manualTrigger`, `log`, `delay`, `runScript`, `fileOperation`, `folderOperation`, `serviceManagement`, `registryOperation`, `wmiQuery`, `startProgram`, `junction`, `restApi`, `sql`, `xmlQuery`, `jsonQuery`, `emailNotification`, `startWorkflow`, `returnData`.

### Edge condition operators (all 14)

- **Numeric:** `==` `!=` `<` `>` `<=` `>=`
- **String:** `contains` `startsWith` `endsWith` `matches`
- **Unary:** `isEmpty` `isNotEmpty` `isTrue` `isFalse`
- **Combinators:** `AND` group, `OR` group, `NOT`
- **Legacy shortcuts:** `stepId.success`, `stepId.failed`

Each Phase E branch in `main.json` is a condition that shows one or more of these operators.

### Junction modes (all 3)

- `waitAll` → `junction-all` (Phase D, waits for 5 parallel branches)
- `waitAny` → `junction-match`, `junction-rest`, `junction-any`, `junction-final` (fires on the first successful branch)
- `waitNofM` → `junction-nofm` (`requiredCount: 2` of 3 branches)

### Further features

- **Retry policy:** `collect-host` (exponential 2x), `prog-cmd` (linear 3x), `rest-get` (exponential 3x)
- **Step timeouts:** `collect-host` (60s), `rest-get` (15s), remote activities (30s)
- **Node-level `disabled`:** `log-disabled-node` → appears as `Skipped`
- **Node-level `breakpoint`:** `log-breakpoint` → pauses in the debug run
- **Edge-level `disabled`:** `email-notify` → `log-nowhere` edge never fires
- **Error path:** `rest-get.failed` → `log-rest-fail` (demo of on-failure branching)
- **Sub-workflow:** `invoke-child` calls the `child.json` workflow, whose `returnData` comes back as `{{child.param.*}}`

## Testing in the UI

1. Open the workflow (the link comes from the seed script).
2. **Layout check:** LTR, no overlapping edges, 11 phases clearly separated from each other.
3. Click **Run** → parameter dialog with defaults:
   - `environment` = `staging` (leave as is to skip `log-eq-neq`; set to `production` to fire the branch)
   - `threshold` = `80` (disk-alarm threshold)
   - `dryRun` = `false`
   - `pattern` = `Windows.*`
4. **Start** → live status via SignalR, ~15 seconds runtime.
5. After the run: open **execution details**, check the step status:
   - `log-disabled-node` should be `Skipped`
   - `log-nowhere` should not be in the step list (edge disabled → node unreachable → skipped)
   - `email-notify` fails (no SMTP configured), that is expected — the "Always" edge continues
   - `rest-get`: the status depends on whether httpbin.org is reachable, both paths (success→SQL, failure→log) lead back to the main path via `junction-rest`

**Debug test:** "Run with Debug" → the workflow pauses at `log-breakpoint` → the variable inspector shows `host.param.*`, `rest.param.*` etc. → "Continue" runs on.

## Verified engine details (important when editing)

| Detail | Value |
|---|---|
| `waitNofM` config key | `requiredCount` (NOT `n` — an unknown key falls back to the default 1) |
| Boolean truthiness | Empty, `"false"` (case-insensitive), `"0"` = falsy. Everything else truthy. PowerShell `[string]$true` = `"True"` → truthy ✓ |
| `runScript` param capture | All locally declared variables are exposed automatically as `param.*` (ProcessExecutionEngine appends a capture block to the script). No marker needed. |
| `{{var.output}}` in script text | Inserted as a PowerShell single-quoted string (`'value'`). Do not add quotes yourself. |
| `targetMachineId: "localhost"` | Triggers the localhost bypass → runs in-process without a WinRM session. Ideal for the demo. |
