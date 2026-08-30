# Sample & test workflows

A curated, importable set of example workflows covering **every** one of the 27 activities and 6
triggers — individually and in combination. Import through the UI (`Import`) or
`POST /api/workflows/import`.

> **Looking for the test suite?** The `muster-*.json` bundles and
> `continuous-test-1min/` described below are **samples**, not the regression suite. The suite
> that actually runs on a cadence, asserts its results and is guarded in CI lives in
> [`scripts/test-suite/`](test-suite/) — see [docs/workflow-tests.md](../docs/workflow-tests.md).
> Unlike the samples it drives the passive triggers for real, splits expected failures into
> their own contract, and exercises the invasive service and scheduled-task actions that the
> samples deliberately leave out.

## The clean sample set

| File | Contents |
|---|---|
| `muster-alle-aktivitaeten.json` | **The master**: one workflow that exercises all 27 activities in variation (all 14 edge operators, AND/OR/NOT, disabled edges/nodes, retry, all 3 junction modes, decision/forEach/startWorkflow) plus a child. Roots: manualTrigger (active) + scheduleTrigger (disabled, for demonstration). |
| `muster-einzeltests.json` | **33 individual tests** — one workflow per activity (`Test — <activity>`) and per trigger. Each activity workflow runs through **all (safe) variants** of its activity (for example fileOperation create/exists/copy/rename/move/delete, folderOperation plus list, fileHash SHA256/SHA1/MD5/SHA384/SHA512, zipOperation compress+extract with all 3 compression levels, registry all 8 operations + all 6 value types, textFileEdit all 6 operations + encoding/lineEnding/occurrences/appendIfMissing/backupSuffix/dryRun, waitForCondition script/pathExists/serviceRunning/portOpen/httpOk, restApi all 6 HTTP methods, wmiQuery query/wql/invokeMethod, xml/jsonQuery inline+file × single+all, generateText all 7 modes, junction all 3 modes, forEach auto/json/lines, runScript engine+isolated+transcript+successExitCodes, startWorkflow wait+fire-and-forget). Destructive variants (serviceManagement stop/restart/create/delete, powerManagement shutdown/restart, scheduledTask register/unregister) are **not** run against real resources. Plus a shared `Muster Test: Child`. |
| `muster-kombinationen.json` | **Combinations/topology**: `Muster — Trigger → Databus` (proving that trigger output parameters land on the data bus), `Muster — Variable-Pipe` (passing data through runScript→jsonQuery→decision) and `Muster — Sub-Workflow (Parent)` + child (startWorkflow + forEach fan-out). |

Remote activities use `targetMachineId: "localhost"` → they run **in-process through the localhost
bypass** on the API host, so they are genuinely executable without a WinRM target.

**Environment-dependent nodes** (the configuration is correct, execution depends on the host and
configuration): `emailNotification` (needs SMTP), `llmQuery` (needs `Llm:Enabled=true`),
`scheduledTask` (needs a working Task Scheduler CIM provider on the target).

**Self-probing your own API — mind the port.** The two network probes in
`Test — waitForCondition` (`portOpen` / `httpOk`) target NodePilot's own API. Their targets live on
the workflow as the trigger parameters `probePort`/`probeUrl` and are preset to the **development**
port `5000` (`launchSettings.json`). On an installation, Kestrel listens on the HTTPS port chosen
during setup — so when running there, set for example
`probeUrl = https://localhost:<HTTPS-port>/healthz/live` and `probePort = <HTTPS-port>`, otherwise
both steps correctly run into their timeout. The host additionally has to be listed in
`WaitForCondition:AllowedHosts` (default: `localhost`); `RestApi:*` is **not** responsible for these
probes.

The `restApi` nodes of `Test — restApi` target `http://localhost:5000` for the same reason. On a
hardened installation they are blocked **deliberately**: `RestApi:BlockPrivateNetworks` is `true` in
production and `RestApi:AllowedHosts` is empty. To run them there, add the host to
`RestApi:AllowedHosts` consciously (which requires a restart) — that opens outbound HTTP to loopback
and is therefore its own decision, not a side effect of the probe list.

**Variants deliberately not included** — on a normal host they would be a permanently red step rather
than a test:

| Variant | Reason |
|---|---|
| `runScript` with `engine: pwsh` | Requires PowerShell 7 to be installed |
| `startProgram` with `useShellExecute: true` | Blocked under production defaults by `StartProgram:DisallowShellExecute` |
| `sql` with `provider: sqlserver`/`postgres` | Needs a reachable database; without a `connectionRef`, a password would sit in the workflow JSON (which the export redacts to `***` anyway) |
| `powerManagement` other than `abort` | Shuts the host down |
| `serviceManagement` stop/restart/create/delete/setStartType, `scheduledTask` register/unregister/start/stop/enable/disable | They change real system state; at one-minute intervals that would be >1,400 service/task mutations per day |

## Continuous run

`scripts/continuous-test-1min/` installs 10 orchestrators that together start 30 of these test
workflows **every minute** through `startWorkflow` (fire and forget). Details:
`scripts/continuous-test-1min/README.md`. The five background trigger tests **cannot** be driven from
there — `startWorkflow` takes the engine path, not the trigger path.

Pure background trigger tests (`scheduleTrigger`/`webhookTrigger`/`databaseTrigger`/
`fileWatcherTrigger`/`eventLogTrigger`) are imported **disabled**, so that they do not fire or poll in
the background — simply enable them to test.

## Trigger output parameters on the data bus

Every trigger publishes its event data onto the data bus. Verified (fileWatcher → `filePath`/
`fileName`/`fileAction`, webhook → `webhookBody`/`webhookMethod`/`webhookPath` + JSONPath
`fieldMappings`, manual → the declared parameters). **Read them through
`{{<triggerNode>.param.<key>}}`** — that is the universal, contract-correct route that resolves in
engine-local configurations (log/returnData). `{{manual.<key>}}` is a flat runScript variable and
stays literal in configurations — which is why all trigger samples use `{{trg.param.X}}`.

> **Important:** trigger-fired runs need an *effective principal* (`Workflow.PublishedByUserId`),
> otherwise they are aborted with `missing_effective_principal` (enterprise SSO hardening). Import does
> not set it — **publish** the workflow (not just enable it), or set `PublishedByUserId`, and then
> trigger runs go through cleanly.

## Reference / anchor (not part of the import set)

- `test-master-all-activities.json` — the living **style-guide reference example** (see `docs/workflow-styleguide.md`)
  and the few-shot for AI workflow generation (`src/NodePilot.Ai/Prompts/workflow-example.json`). Do not modify.

## Realistic operational examples (hand-built)

- `example-windows-update-health-workflow.json` — a Windows Update health check of a host (CBS/WU log tails, service/registry/WMI/file-system probes, `decision` classification).
- `endsystem-log-korrelation-workflow.json` — **hourly AI log correlation** across three end systems: an SCCM server (CCM/CBS/VSS), a billing block (configurable application logs) and a PostgreSQL database server. A `scheduleTrigger` (`0 0 * * * ? *`) plus a manual run; per system a `runScript` collector → `llmQuery` triage, then `waitAll` → `llmQuery` correlation (`jsonMode`) → `jsonQuery` (severity/summary) → `decision` → log + `returnData`. The configuration (hosts, log paths, tail, error regex) is in the **CONFIG block of the `init` node**. It needs `Llm:Enabled=true`; the three target hosts are placeholder host names → for a real WinRM run, register them through `/api/machines` + `/api/credentials` and enter them in the CONFIG block.

## Creative demo workflows (hand-built)

`example-uboot-workflow.json`, `decorative-flower.json`.
(Further local demo workflows are deliberately gitignored and not part of the repository.)
