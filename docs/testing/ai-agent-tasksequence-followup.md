# Task sequence diagnosis follow-up

Date: 2026-09-20. Branch: `feature/ai-agent-activities`.

Scope: increase the shared team model-call ceiling from 40 to 100, improve the
diagnoses of cases 2 and 5 from `ai-agent-tasksequence-blind-tests.md`, and repeat
the live read-only investigations. Other cases are not implicitly regraded.

The active dev configuration and the Core, API DTO and UI defaults now use 100
team model calls. Tool budget remains 500 and context remains 250,000 characters.
An explicitly lower workflow override still applies; the fresh retest workflows
request 100. Single-agent defaults remain unchanged.

## Generalization constraint

No runtime branch, heuristic classifier, test ID, expected diagnosis, injected
filename or predetermined repair is added to the product or skill. Changes describe
how to obtain and reconcile evidence. Tests continue to inject faults outside the
agent host; agents receive only the operation, targets, identifiers and time window.
All runtime actions remain read-only.

For case 2, the captured run lacked the application-to-pool mapping and confused
current blocking evidence with historical change attribution. The existing shell
tool now permits the read-only WebAdministration `Get-WebApplication` command with
literal Name/Site arguments. Other parameters, modifying commands and target hops
remain blocked. The skill explains tracing actual configuration dependencies and
keeping a present blocker separate from the unknown initiating actor.

The first retest with skill 1.0.18 was cancelled after 62 model calls because it
repeated large overlapping log reads, hit member context limits and still lacked
the decisive client output. The fault was restored and a healthy reference passed.
This attempt is retained under `.runlogs/agent-tasksequence-followup/attempt1` and
does not count as a success. Version 1.0.19 adds a general bounded contiguous-window
reading strategy using the existing PowerShell tool. No error-specific keyword or
expected answer is supplied.

## Automated verification

- Regression first: Get-WebApplication was rejected (1 failed, 19 passed).
- After the narrow permission change: targeted reads, denied writes and deadline
  tests passed (26 tests).
- Full Engine agent test group: 279 passed on repeat. The preceding broader run
  had one deadline-related failure; that exact test passed in the focused repeat.
- API build succeeded; existing compiler warnings remain.

The running dev backend was rebuilt and restarted; login and settings API checks
confirmed availability and the persisted 100-call limit. `/health` is not a route
in this instance (404); authenticated API success was used for readiness.

## Live retests

Case 2 succeeded with skill 1.0.19: 36 model calls, 101 tool calls, 6 delegations,
235.3 seconds. The client read the actual HTTP-503 output and URL; the server reviewer
independently mapped that application to its pool and observed the pool stopped.
The final report states the current concrete blocker and targeted remedy. It leaves
exact stop time and initiating actor unresolved; these are not prerequisites for
the bounded current-state diagnosis. The healthy post-restoration reference passed.

[Case 2 workflow](http://localhost:5173/workflows/49179694-d1f2-4265-9733-f99fd9778764)
— execution `4405c7e5-63da-48f8-b86c-3a25c8eebd0b`.

For case 5, the previous journal already contained both inventories, but the members
failed to reconcile them. Version 1.0.20 adds a general method for comparing keyed
sets and their properties, preserving version/scope and checking missing/extra items
before treating one matching hash as evidence of completeness. It asks members to
exchange the actual comparable values and reviewers to verify the decisive difference.
This introduces no diagnostic outcome rule, product-specific error mapping or
expected test filename.

The first case-5 retest with 1.0.20 finished without finding the missing item. Its
reviewers accepted an aggregate integrity mismatch with the underlying difference
still unresolved. This is retained in `attempt2` and is not graded as success.

The general supervisor instructions now explicitly reconcile members' concrete
observations by identity, version and scope, including inventory membership. They
ask for the counterpart's actual observations in a focused follow-up and avoid
substituting repeated health summaries for comparison. This applies to any team,
without a selected diagnostic skill. There is no object-specific classifier or
automatic diagnosis. Runtime/team/configuration tests pass (48 tests).

The final backend build succeeded with zero warnings/errors in the incremental
build. Background process creation was rejected by the execution policy; a direct,
supervised `dotnet run --no-build --project E:\NodePilot\src\NodePilot.Api` start
was permitted, and authenticated settings checks confirmed readiness.

The final case-5 retest used the new generic supervisor instructions and skill
1.0.20. It took 47 model calls, 131 tool calls, 8 delegations and 277.6 seconds.
The reviewers inspected the actual DP FileLib payload, compared it with source
and client data, and independently found the missing client file. The final report
names that absence but still declines to connect it to the native aggregate
content-hash failure. Therefore the strict grade remains **partial**, improved
from the original missed file; this is not reported as full causal success.
The remaining review gate also required a redundant server observation after a
client reviewer had supplied new relevant evidence. That behavior was not changed.

[Case 5 workflow](http://localhost:5173/workflows/f01fa330-aa1c-4339-9151-a5bd0fa40b17)
— execution `2611f535-55ce-48da-88b3-a0cc7c64610f`.

## Final verification and remaining cases

All four investigations (including the cancelled and unsuccessful intermediate
attempts) are retained, terminal and disabled. Each fault was restored and followed
by a successful real task sequence run. Eight execution windows on CLIENT1/CM1
were checked: no MsiInstaller or WindowsUpdateClient installation events 19/20/21.
Journal inspection found no modifying shell command. A script request in the first
case-2 attempt was blocked by Restricted execution policy; the policy was not changed.

The SCCM test deployment CHQ2000B was removed and task sequence CHQ0000B disabled.
The original cache quota, services, source content and WSUS configuration were
restored. WSUS and MP returned HTTP 200; DP status was 0 and the original payload
hash matched. Recovery tasks and temporary host-access adapter/firewall were removed;
TrustedHosts is back to `localhost`. The dev API remains running with the updated
code and persisted team limit of 100. Existing workflow overrides below 100 are not
silently rewritten. Raw evidence and verification scripts are under
`.runlogs/agent-tasksequence-followup/`.

| Original case | Remaining gap |
|---|---|
| 3 | Wrong WSUS port was identified by specialists but review exhausted the old model budget. Not retested; 100 calls alone is not proof of resolution. |
| 5 | Missing cached file now found; causal connection to the native aggregate hash failure remains incomplete in the final report. |
| 6 | Altered cached manifest was not precisely isolated. Not retested. |
| 7 | Missing physical DP blob was not identified. Not retested. |
| 9 | Corrupted physical DP blob was not isolated. Not retested. |
| 10 | Exact bad source path was found, but missing path versus access failure remained unresolved. UNC restriction unchanged; not retested. |

The results support improvements on these observed runs, not a guarantee of equal
performance on every similar incident. Case 2 is the only formerly incomplete case
promoted to complete current-cause diagnosis in this follow-up.

Subsequent runtime/skill corrections and new blind retests are recorded in
[Client cache diagnosis follow-up](ai-agent-cache-validation.md). This table retains
the results at the end of the earlier follow-up; it is not the latest acceptance list.
