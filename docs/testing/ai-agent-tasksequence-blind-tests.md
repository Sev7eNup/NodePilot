# Live SCCM task sequence blind tests

Date: 2026-09-20. Branch: `feature/ai-agent-activities`.

All ten blind agent investigations have finished. Strict diagnostic scoring:
**3 complete, 4 partial, 2 missed root causes and 1 failed final report**.
Nine workflows technically succeeded; that does not mean nine correct diagnoses.
All ten restoration reference runs succeeded. Cleanup and the event audit passed.

## Isolated test assets

- Task sequence: **CHQ0000B**, `NP Task Sequence Validation 20260920`.
- Package: **CHQ0000A**, `NP Task Sequence Test Content`.
- Source: `\\CM1.corp.contoso.com\Packages$\NP-TaskSequence-20260920`.
- Collection: **CHQ00015**, exactly one member, **CLIENT1 / 16777220**.
- Required, nonrecurring test deployment: **CHQ20009** for cases 1–8 and
  **CHQ2000A** for cases 9–10. The first available
  deployment CHQ20008 was removed when remote Software Center execution required
  an interactive user. Subsequent controller starts trigger only the test deployment's
  scheduled message; the agents cannot start task sequences.
- Unique 24 MiB random payload plus a SHA-256 manifest and a harmless required text
  file. No application installer, update installation, OS deployment or reboot.

The six real SCCM steps check the target, download package content to the client
cache, verify package files, check update configuration, query the configured WSUS
ClientWebService endpoint and query the management point. The healthy baseline
completed all six steps with exit code 0; both endpoint probes returned HTTP 200.

These are update **prerequisite/connectivity** checks, not an Install Software
Updates step or a WUA scan. A manifest failure inside a Run Command Line step must
not be reported as a native SCCM download hash failure.

## Blind diagnosis and scoring

Each case gets a fresh published NodePilot workflow with a supervisor, CLIENT1
specialist, CM1 specialist and one independently querying reviewer per machine.
Members use the existing read-only PowerShell and file tools and the optional
Windows diagnostic skill 1.0.17. Targets and credentials are fixed in configuration.
Budgets: 40 model calls, 500 tool calls, 20 delegations, 1,100 seconds.

The task exposes only the affected operation, site, machine names, package/task
sequence/deployment IDs and UTC start of the current attempt. It does not expose
the injected change, restoration command or expected answer. Agents must identify
the failing stage, causal evidence, a minimal proposed remedy and verification.
Workflow completion by itself does not count as correct diagnosis.

The controller injects one fault at a time. It captures actual SCCM output, retains
the fault during investigation, restores it and requires a successful reference
run before proceeding. Configuration changes have a 30-minute recovery task;
explicit restoration removes it. The native hash case checks exclusive ownership
of the unique test blob before modifying one byte and restores that exact byte.
No shared production content is intentionally modified.

## Evidence

Local, ignored artifacts: `.runlogs/agent-tasksequence/`:

- `assets.json`, `deployment.json`, `baseline-smsts.log`.
- `plan-N.json`, `injected-N.json`, `fault-present-N.json`, `fault-restored-N.json`.
- `ts-caseNN-*`, `ts-restoredNN-*`: actual task sequence and execution-manager logs.
- `case-NN-active.json`, `case-NN-result.json`: workflow configuration, full run and
  paginated agent journal. Reports and tool errors are extracted separately.

Two preparation attempts are retained separately and excluded from diagnosis
scores: an excessive per-run model budget rejected before execution, and an
aborted attempt with missing temporary WinRM TrustedHosts entries. Subsequent
starts verify both machine connections first.

Additional preparation probes are excluded: a 10 MiB cache quota did not fail the
TS; the first missing-DP-file probe reused pinned client content; the first
distribution-removal probe ran before asynchronous removal finished. The harness
then asserted eviction of only the test package and waited for removal. These
successful probes are not counted as fault-detection successes.

## Results

"Complete" requires the actual injected cause, causal evidence and a targeted
proposed remedy. "Partial" means a relevant diagnosis with unresolved causal
detail. Agents were never given the fault plan. Each case used fresh sessions;
the real SCCM logs retained earlier history.

| # | Actual injected fault | Native observation | Agent finding and proposed remedy | Grade |
|---|---|---|---|---|
| 1 | CLIENT1 `wuauserv` disabled | Step 04 fails on disabled service | Identifies exact service state; restore startup and verify TS | Complete |
| 2 | CM1 `WsusPool` stopped | Step 05 receives HTTP 503 | Finds both stopped pool and 503, but leaves the causal link merely plausible and remedy conditional | Partial |
| 3 | CLIENT1 WUServer uses port 18530 instead of CM1's 8530 | Step 05 connection timeout | Specialists find endpoint mismatch; review does not converge; 40 model calls exhausted, no final report | Failed |
| 4 | CLIENT1 outbound firewall blocks CM1 TCP 8530 | Step 05 cannot connect | Identifies exact active rule, scope and healthy server; proposes removing the specific block | Complete |
| 5 | `required.txt` removed from the test package in CLIENT1 cache | Native `0x80091007` before script 03 | Finds hash failure but misses the missing cached file and explores DP hypotheses | Missed cause |
| 6 | Cached `manifest.sha256` replaced with 64 zeroes | Native `0x80091007` before script 03 | Identifies cached-content mismatch and proposes scoped cache renewal; does not locate altered manifest | Partial |
| 7 | Uniquely owned payload blob removed from CM1 FileLib | Native HTTP 404 / `0x80190194`, subsequent step 03 timeout | Finds download failure, but does not identify missing physical DP blob | Missed cause |
| 8 | Test package distribution to CM1 removed | CLIENT1 `WaitingContent`, no matching DP | Identifies removed distribution and absent assignment; proposes targeted redistribution | Complete |
| 9 | One byte corrupted in the uniquely owned CM1 FileLib payload | Native `0x80091007` after fresh download | Finds hash conflict; recommends DP/source comparison but does not perform the decisive physical-file comparison | Partial |
| 10 | Test package source points to a nonexistent directory on CM1 | `distmgr` source error 2, incomplete DP content, CLIENT1 `WaitingContent` | Identifies exact source path and causal chain; proposes source correction and redistribution, but leaves missing path versus access failure unresolved | Partial |

Cases 5, 6 and 9 genuinely fail SCCM's native content verification before the
custom file-check script executes. The package aggregate hash is not the same
quantity as an individual file's SHA-256; they must not be compared directly.

Case 10's direct UNC checks were rejected by the current local-path restriction.
That restriction contributed to the incomplete distinction. Case 5, in contrast,
had no permission denials: its failure was investigation strategy, not read access.

## Gaps exposed by this suite

1. **Review convergence:** case 3 fails without a final report despite useful
   specialist evidence; case 8 consumes all 40 model calls. The 500-tool budget
   is not the bottleneck. Review gates sometimes reject closure despite relevant
   evidence collected by another member.
2. **End-to-end content evidence:** compare source, actual DP FileLib/DataLib and
   actual client cache. A healthy source hash or DP summary `State=0` does not
   prove that the file served by the DP is intact. This calls for reusable
   diagnostic knowledge, rather than an answer hardcoded for each injected fault.
3. **Current attempt selection:** active TS logs can be under
   `CCM\Logs\SMSTSLog\smsts.log`; the root `smsts.log` can describe an older run.
   `WaitingContent` can occur before any new TS log exists. Agents must correlate
   current execmgr/CAS evidence rather than force every incident into a TS step.
4. **Time-format interpretation:** CMTrace bias suffixes and IIS UTC need explicit
   format knowledge. Some reviewers rejected valid correlation even when paired
   GMTDATE evidence was available. This is distinct from a demonstrated failure
   of the previously fixed CIM timestamp conversion.
5. **Read-only coverage and error clarity:** direct UNC reads and some SCCM CIM
   classes are blocked; other failures are guessed nonexistent log paths.
   A generic policy error mentioning `Win32_Product` is not proof that the agent
   actually queried that class. Permission improvements must retain the MSI
   side-effect protection.

No product code was changed during this validation. Existing skill version 1.0.17
was used throughout, without feeding test-specific solutions to the agents.

## Read-only audit and restoration

The ten agent investigations used 327 model calls, 1,065 tool calls and 64
delegations in 2,378.4 seconds (39.6 minutes of agent runtime, excluding lab
preparation and reference runs). The tool-call count includes delegation and skill
reads. There were 246 PowerShell calls and 29 tool errors. The observed tool set
was PowerShell, file list/read/search, skill loading/resource reading and delegation;
no skill script execution or repair tool was used.

Journal command inspection found read/query commands and formatting pipelines,
with no modifying command, `Win32_Product`, MSI invocation or arbitrary method
execution. Both machines were queried for MsiInstaller events and Windows Update
installation events 19/20/21 in each of the ten agent execution windows: **20
windows, zero matching events**. This is evidence for these runs, not a universal
proof against all conceivable operating-system side effects. The controller's
authorized injections, SCCM downloads and restoration are separate from the
read-only agent investigations.

Verified final state at approximately 20:26 UTC:

- All ten injected faults restored, each followed by a successful real TS run.
- Original package source restored; DP distribution successful (`State=0`, final
  source version 2); physical payload SHA-256 restored to
  `3AAD819EAC7DD3DD91C9C904A5E1D6280CC08A8036B02233A0B88B8A1BDBA220`.
- WSUS and MP HTTP probes both return 200; WsusPool started; original WUServer
  restored; fault firewall rule and recovery tasks removed.
- CLIENT1 cache quota restored to 20,480 MiB; BITS and wuauserv restored to their
  observed initial Manual/Stopped states; CcmExec remains running.
- All SCCM deployments of CHQ0000B removed and task sequence disabled. Package,
  collection and disabled TS retained as reviewable test assets.
- All ten NodePilot workflows disabled; their run journals remain available.
- Temporary CLIENT1 host-access adapter/firewall removed; host TrustedHosts
  restored to `localhost`.

Restoration details: after case 8 the original pending request resumed and
completed, and deployment CHQ20009 was replaced with CHQ2000A for a fresh healthy
reference. After case 10 the source update succeeded but a redundant
`Start-CMContentDistribution` was rejected because the assignment already existed.
The controller verified source and distribution before removing its recovery
task. CLIENT1 policy refresh then allowed the final successful reference at
20:25:06 UTC. Initial 100-second WaitingContent observations are retained and are
not represented as successful reference runs.

Machine-readable verification is in `.runlogs/agent-tasksequence/metrics.json`,
`shell-calls.json`, `tool-failures.json`, `event-audit.json`, `cleanup-*.json` and
the ten `ts-restoredNN-status.json` files.

## Run references

All links refer to the local development instance.

| Case | Workflow | Execution ID | Models / tools / delegations | Seconds |
|---|---|---|---|---|
| case-01 | [Open](http://localhost:5173/workflows/5f9eb163-5d20-430a-9e9f-d1597e1a9a62) | `2634379a-560b-4e82-832f-9450d79200b0` | 27 / 92 / 4 | 179.6 |
| case-02 | [Open](http://localhost:5173/workflows/cc169eb6-e1c3-4f8d-a44b-5867b24fd610) | `d544cc2d-9ca6-46db-8fe4-8e2a1f4f57fb` | 29 / 97 / 6 | 191.0 |
| case-03 | [Open](http://localhost:5173/workflows/34e2e698-71b6-46d0-ae7b-976c3fef51dd) | `bfe37fa1-b747-435d-8c93-4c3df6b4755d` | 40 / 103 / 8 | 288.8 |
| case-04 | [Open](http://localhost:5173/workflows/b8b743a2-5bec-43b4-9aba-ac822725f6a3) | `ac1e28b6-24ad-481d-be6a-401a38b3687b` | 28 / 107 / 4 | 216.0 |
| case-05 | [Open](http://localhost:5173/workflows/0047c0da-b88c-40b4-9b67-13129d29875f) | `ffe8f311-1b77-4d8f-b065-6ca12343ebf4` | 37 / 96 / 8 | 234.2 |
| case-06 | [Open](http://localhost:5173/workflows/104b2e5d-4576-4ba7-868e-8f8ba7cdb874) | `d3135569-8cbb-44d1-b93b-2481290b164f` | 32 / 112 / 7 | 241.3 |
| case-07 | [Open](http://localhost:5173/workflows/ca7ff422-c451-4536-8915-fa61d92736f5) | `66dd98fe-0809-4e07-b60d-63942f4026b7` | 33 / 116 / 7 | 248.4 |
| case-08 | [Open](http://localhost:5173/workflows/a4b67b12-6563-4905-9380-321f15b4125c) | `7a32f1bb-f668-4619-8bcd-df4dae97da0c` | 40 / 154 / 8 | 344.2 |
| case-09 | [Open](http://localhost:5173/workflows/10f92bc0-369f-4efe-b295-3f865415b057) | `ffd45767-814f-4e28-874c-c5bb1ddafc0d` | 37 / 111 / 8 | 246.1 |
| case-10 | [Open](http://localhost:5173/workflows/7a388a53-289d-4df3-a311-dd68d02e04f8) | `234e61c0-06c7-4d28-9805-4a7312ffa4bc` | 24 / 77 / 4 | 188.8 |
