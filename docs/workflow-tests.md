# NodePilot Test Suite

51 generated workflows under [`scripts/test-suite/`](../scripts/test-suite/) that run every
activity variant, every trigger, every edge operator and every retry backoff **on a fixed cadence against
the running engine** and check the result. Live installation in the folder
`/Test_Workflows`.

The difference from the three predecessor generations (`scripts/test-runbooks/`,
`scripts/muster-*.json`, `scripts/continuous-test-1min/`, all superseded): the suite is
**generated**, it is **self-checking**, and a guard test in CI proves that every
claimed coverage is actually reachable and checked.

## Two Contracts

The engine has no notion of a "handled failure": `WorkflowEngine` counts all failed
steps at the end and sets the run to `Failed` on any hit — a `.failed` edge keeps routing
but does not rescue the status. An intentional failure therefore cannot live in a workflow
that is supposed to stay green.

| Contract | Namespace | Expected status | Who judges |
|---|---|---|---|
| **positive** | `[TestSuite] <type>` | `Succeeded` | the workflow itself: an `assert` node throws on deviation |
| **negative** | `[TestSuite-Neg] <area>` | `Failed`, with **exactly** the declared failing steps | `Verify-TestSuite.ps1` against the manifest |
| **invasive** | `[TestSuite-Inv] <type>` | `Succeeded` | like positive, but only on hosts that have enabled it |

For the negative contract the verifier checks the **set of failed StepIds**, not just
the status. A run that fails for a different reason counts as a defect of the suite.

## `suite-manifest.json` is the coverage source

Not the workflow JSONs and not this file, but
[`scripts/test-suite/suite-manifest.json`](../scripts/test-suite/suite-manifest.json).
It is input to the guard test **and** to the verifier. Every case names dimension, value,
profile, expected outcome, workflow, node and who checks it.

**Four profiles:**

| Profile | Meaning |
|---|---|
| `continuous` | runs everywhere, no external prerequisite |
| `integration` | needs a second system (SMTP sink, database, LLM endpoint, proxy) or production hardening; if it is missing, the workflow is **installed but disabled** instead of red |
| `invasive` | changes the host (create a service, register a task, cancel a pending shutdown); opt-in via `NP_TESTSUITE_INVASIVE`. `powerManagement` additionally needs `NP_TESTSUITE_DISPOSABLE_HOST`, because its `abort` also cancels a restart that a maintenance window scheduled |
| `excluded` | never runs — every entry carries `reason` and, where available, `coveredBy` pointing to the unit test that covers it instead |

Deliberately excluded are, among others: `delay > 86400` (clamps to 24 h and would block a
runner slot), four of the five `powerManagement` actions (they take the host down),
`scheduledTask runLevel: highest` (the result depends on the privilege level, so it is not
deterministic), `runScript engine=pwsh` (PowerShell 7 is not guaranteed),
`restApi` redirects and the 16 MiB cap.

## Conventions

| Aspect | Rule |
|---|---|
| **Generated** | The source is the `spec_*.py` modules; the JSONs are artefacts. A manual change is overwritten by the next `build_suite.py`. |
| **Naming** | `[TestSuite] <type>` / `[TestSuite-Neg] <area>` / `[TestSuite-Inv] <type>` — sort anchor and filter hook. |
| **Folder** | `/Test_Workflows`. |
| **Target machine** | `targetMachineId: "localhost"` — in-proc bypass, no credentials, no WinRM. Exception: the two `script parity remote` workflows target `{{globals.NP_TESTSUITE_REMOTE_MACHINE}}`. |
| **Assertion** | `Variants → cleanup → assert(runScript) → returnData`. The `assert` node is a graph descendant of all variants and reads their results directly; `runScript` success is error-based, a `throw` turns the run red. |
| **Cleanup before assertion** | so that a red assertion leaves no leftovers. Harmless, because the assertion reads the data bus, not the disk. |
| **Own `outputVariable`** | per variant node; otherwise same-named params collide in the junction merge. |
| **Ancestor references only** | a reference to a parallel branch never resolves and is a hard error in `runScript`. |
| **Parallelism** | `MaxConcurrentExecutions = 1` per workflow, set via `PUT /{id}/concurrency-limit`. |

### Sandbox

| Resource | Rule |
|---|---|
| Run sandbox | `C:\Temp\NP-TestSuite\runs\<cid>\`, `HKCU:\SOFTWARE\NP-TestSuite\<cid>`, service `NPTestSvc_<cid8>`, task `\NodePilot-TestSuite\<cid8>` — gone at the end of every run |
| **Permanent fixtures** | `C:\Temp\NP-TestSuite\runtime\{watch,db,acks}\` — **excluded from cleanup**. The file-watcher path and the sentinel database must be preserved for the running trigger sources. |
| Janitor | the first node of every run removes leftovers older than 1 h, orphaned `NPTestSvc_*` services and tasks under `\NodePilot-TestSuite\` |
| Residue check | as a `runScript` node **in the API process** — the `HKCU` of the service account is not the administrator's |

The correlation-ID approach (`generateText mode=guid` as the second node) is the reason why
two overlapping runs never collide on the same name.

## Triggers: the ack protocol

The five passive triggers have no cadence of their own and cannot sensibly be started by
hand either — a manually started run carries none of the trigger parameters the
workflow is supposed to prove. An authenticated callback into the API is ruled out:
`/api/executions` requires `[Authorize]`, a `restApi` step has no durable session, and
an admin token does not belong in workflow JSON.

Instead: correlation ID plus acknowledgement files:

```
[TestSuite] trigger drivers   (every 10 min)
  └─ create cid
  └─ write file runtime/watch/<cid>.txt                → fileWatcherTrigger
  └─ set sentinel in runtime/db/sentinel.sqlite to <cid> → databaseTrigger
  └─ POST to /api/webhooks/<id>/suite with {"cid":…}   → webhookTrigger
  └─ startWorkflow on the manual-trigger workflow      → manualTrigger
  └─ wait 60 s
  └─ collect runtime/acks/<type>/<cid>
```

Each triggered workflow reads its correlation ID from its own trigger parameters and
writes the acknowledgement. For the `databaseTrigger` the ID **is** the sentinel value — the source
passes on nothing else. The ID must parse as a GUID; the acknowledgement file is named after its
canonical form, so a payload cannot point the write outside `runtime/acks/<type>/`.

**One missed round is tolerated.** `DatabaseTriggerSource` deliberately re-baselines the first
observation after every start without firing; after an API restart, exactly one
acknowledgement is therefore missing. Only the second consecutive round makes the driver throw (counter in
`runtime/acks/consecutive-misses.txt`).

The webhook secret is **not** in the repo: the generated file carries a placeholder that
`Install-TestSuite.ps1` replaces during installation with the value of the secret global variable
`NP_TESTSUITE_WEBHOOK_SECRET`. The API never returns a secret value, so every installation
generates a new one from a CSPRNG and writes it to both the global and the webhook definition.

## Cadence

One `scheduleTrigger` per workflow, 7-field Quartz cron, minute offsets staggered.

| Tier | Cron | Content |
|---|---|---|
| A — 5 min | `0 0/5` … `0 4/5 * * * ? *` | light engine types, file system, ControlFlow, edge conditions, variable resolution |
| B — 15 min | `0 5/15`, `0 10/15 * * * ? *` | zipOperation, sql, the five negative workflows |
| C — 30 min | `0 7/30`, `0 22/30 * * * ? *` | integration and invasive |
| D — 10 min | `0 3/10 * * * ? *` | trigger drivers |

`build_suite.py` aborts if a workflow budgets more than **half** of its interval — beyond that
the next run piles up behind `MaxConcurrentExecutions=1` as
`DeferredByConcurrencyLimit` instead of failing loudly.

## Operation

```powershell
# Generate (after every spec change)
python scripts/test-suite/build_suite.py

# Install - idempotent, creates missing globals
./scripts/test-suite/Install-TestSuite.ps1 -Password '<admin>'
./scripts/test-suite/Install-TestSuite.ps1 -Password '<admin>' -Profiles continuous,integration,invasive

# Judge
./scripts/test-suite/Verify-TestSuite.ps1 -Password '<admin>' -Once            # start now
./scripts/test-suite/Verify-TestSuite.ps1 -Password '<admin>' -WindowMinutes 60  # acceptance

# Single workflow as an assertion
np workflow run '[TestSuite] textFileEdit' --wait     # exit 0 = all assertions held
```

Verification requires the `continuous` profile by default. Pass `-Profiles continuous,integration`
or the exact profiles selected during installation when validating additional cases. Missing or
disabled required workflows and zero verified workflows fail the check. Window mode paginates the
execution history and applies every manifest expectation to every completed run in the window.

**On an installed, production-hardened instance** (release gate, `RELEASING.md` step 6)
the suite runs fully green only with three preparations:

| Needs | Why |
|---|---|
| `Trigger:Database:Connections:np-testsuite-sentinel` = `Data Source=C:\Temp\NP-TestSuite\runtime\db\sentinel.sqlite` in `appsettings.Production.json` | Production accepts only named connections for the `databaseTrigger`; Dev ships the key in `appsettings.Development.json` |
| the host's own hostname in `RestApi:AllowedHosts` and `WaitForCondition:AllowedHosts` | restApi and probe cases call the instance itself |
| `-ProbeUrl` pointing to a **different** host (global `NP_TESTSUITE_PROBE_URL`), that host in `WaitForCondition:AllowedHosts` | Windows also resolves its own name to link-local addresses, which the httpOk probe always blocks |

The unknown-path case of the negative suite uses an `/api` path: outside `/api` an
installed instance serves the web app with 200.

**Installer order** (the three traps that hit all predecessors):

1. A workflow created via `POST /api/workflows` is **already checked out by its creator** —
   a following `/lock` returns 409. New → `POST` + `/publish`; existing → `/lock` + `/publish`.
2. `00-child-echo` must exist **before** its parents and be **enabled**: `forEach` and
   `startWorkflow` resolve it by name and fail on a disabled child.
3. `MaxConcurrentExecutions` is not in the publish body and needs its own endpoint.

The import endpoint is deliberately **not** the installation path: it has no update-in-place
and produces `(Imported 2)` on a name collision.

`-RemoveLegacy` removes the superseded `Test — *`, `[Dauertest 1m] *` and `Muster — *`
workflows from the instance. Never automatically.

## Guard Test

[`TestSuiteCoverageTests`](../tests/NodePilot.Engine.Tests/Activities/TestSuiteCoverageTests.cs)
in `NodePilot.Engine.Tests`. It checks more than "the value occurs somewhere" — that is exactly how
the old suite fell apart:

1. every type from `activity-config-reference.json` has at least one case;
2. every case points to a node that exists, is **not disabled** and is **reachable from an
   active trigger** (a real graph walk, no disabled edge on the way);
3. the `assert` node really references the case's `outputVariable` — or that of an
   explicitly named witness (`assertedVia`), such as the `exists` after a `create`;
4. excluded cases carry a reason, and a given `coveredBy` exists;
5. exactly one trigger per workflow, cron from the tier list, runtime budget respected,
   naming scheme, no dangling edges, fan-in only on `junction`, positions on the 20 px grid;
6. no `ack` script reads a variable of its own before assigning it — a name read before
   assignment is always `$null`, and the acknowledgement is then never written.

Cases that explicitly expect `Skipped` (disabled edge, disabled node, unmet
`.failed` condition) are exempt from point 2 — there, unreachability is exactly
the thing under test.

## PowerShell Parity: Local and Remote

`spec_parity.py` establishes that a local script step ends the same way as the same script over WinRM.

| Workflow | Profile | Checks |
|---|---|---|
| `[TestSuite] script parity` | continuous | templates at any position within a word, umlauts over the data bus, Windows modules in the 5.1 process, output as object text without host/warning lines, exit code after `exit` and `return`, working directory as in the pool, temp script already gone during the run, background program does not hold the step, `startProgram` in OEM and UTF-8, `waitForCondition` script in 5.1 |
| `[TestSuite-Neg] script parity` | continuous | every error entry, native stderr, `throw` with preserved values, parse error, exit code after `return`, word with its own quotation marks, non-executable condition, program timeout with partial output |
| `[TestSuite] script parity remote` | integration | the same checks over WinRM as the reference side, plus data bus local → remote → local |
| `[TestSuite-Neg] script parity remote` | integration | the reference failures over WinRM |

The remote workflows need the global `NP_TESTSUITE_REMOTE_MACHINE`: name or hostname of a
registered machine with a default credential. Without it they are installed but not enabled.
The machine policy `AllSigned` is checked by the release-lab scenario **P1** (`scripts/release-lab/`),
because it changes the host.

## Found and Fixed While Building the Suite

The suite uncovered two engine defects, both in the PowerShell wrapper:

- **`$LASTEXITCODE` survived the reuse of a pool runspace.** Measured on
  `[TestSuite] runScript`: `v5` and `v6` run no native command and still reported
  `exitCode: 3` from `v4`'s `cmd /c exit 3` — across executions, even in a different
  workflow. `successExitCodes` thereby evaluated a foreign value.
- **Injected upstream parameters came back out as outputs of their own** and built up
  along the chain (`v4` stored 24 keys, three of them from the step itself).

Both are fixed: the wrapper resets `$LASTEXITCODE` and `$Error` before the script and
separates injection and user script into two nested scopes, so that `Get-Variable -Scope Local`
returns exactly what was assigned. Guarded by `WrapperScopeAndExitCodeTests` and
`PowerShellReservedVariablesParityTests`.

## Known Limits

- **The `integration` and `invasive` profiles are never executed on a dev instance.**
  `appsettings.Development.json` deliberately relaxes `RejectTraversal`, `DisallowShellExecute`,
  `SqlActivity:RequireConnectionRef` and `Trigger:Database:RequireConnectionRef` — the
  corresponding rejections are not observable on a dev host and therefore live in
  `[TestSuite-Neg] production guards`.
- **`emailNotification`** needs an SMTP sink (e.g. smtp4dev), **`llmQuery`** needs
  `Llm:Enabled` and an active profile, the **event log source** `NodePilot-TestSuite` must be
  registered once, elevated, and the two invasive workflows need a
  privileged service account.
- **`serviceManagement` start/stop/restart** do not run against a service chosen by the suite.
  A cmd.exe fixture never answers the SCM (error 1053); real cycling needs a
  service that the host operator names themselves via `NP_TESTSUITE_SERVICE_TARGET`.

## References

- Workflow JSON format and activity types: [CLAUDE.md](../CLAUDE.md)
- Layout styleguide: [docs/workflow-styleguide.md](workflow-styleguide.md)
- Activity config keys: [docs/claude-reference.md](claude-reference.md)
