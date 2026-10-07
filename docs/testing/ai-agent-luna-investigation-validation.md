# Luna blind server-fault validation — 21 September 2026

Branch: `feature/ai-agent-activities`. Follow-up to
[read access and completion checks](ai-agent-read-budget-validation.md).
Artifacts: `.runlogs/agent-investigation-validation/`.

## Method

All five members (supervisor, client/server specialists and client/server
reviewers) explicitly use `gpt-5.6-luna`. The runner rejects any other model.
The global active profile is also checked after the runs. Budgets remain
100 model calls, 500 tool calls, 20 delegations and 30 minutes per team.
No diagnosis or injected-fault description is supplied to the members.
The agent permissions remain read-only; fault preparation, harmless test
task-sequence execution and restoration are separate controller operations.

The healthy reference completed successfully at 09:43:15 UTC using deployment
CHQ2000F, restricted to CLIENT1. The sequence is case 7, restoration and a
healthy reference, then case 10, restoration and another healthy reference.
The running backend is unchanged during this comparison.

Case 7 removes the isolated test package's physical DP payload, with its original
bytes held for restoration outside the agents' evidence paths. The controller
checks exclusive ownership and the original digest before moving it.

Case 10 changes only the isolated package's source to a nonexistent directory
and requests package processing. DP assignment and existing client content are
retained. Its neutral task asks about current server-side package processing;
it does not presuppose a client failure. A current processing failure must be
observed before starting the team. This avoids the previous mixed fixture of
source failure, DP removal and an old client content version.

Previous server-fault comparisons used Terra. A new result cannot isolate the
effect of product changes from the change to Luna, nor establish a general
success rate from one run per case.

## Results

| Case | Result | Model / tool / delegation calls | Seconds |
|---|---|---:|---:|
| 7 — missing physical DP payload | Failed without a final report; context-summary output limit | 54 / 77 / 5 | 238.4 |
| 10 — nonexistent package source | Cause identified, scoped remedy supplied; verification wording still too rigid | 46 / 86 / 8 | 346.9 |

Case 7's client specialist identified the failed content access. The server
specialist inspected the registered package, source directory and distribution
summary but did not establish the missing physical FileLib object. The server
reviewer instead emphasized a logged HTTP 401 branch without resolving the
other endpoint's HTTP 404. These intermediate messages are not an accepted
diagnosis. Source-directory existence does not demonstrate DP payload existence.

The second client-review delegation triggered context compaction. Model call 53
(`context_summary`) completed; call 54 with the same purpose returned
`finish_reason=length`. The adapter rejected the incomplete answer and cancelled
the run, without dispatching any tools from it. This was the separate summary
client's 2,048-token output cap, not the normal agent's 250,000-token configured
ceiling and not the 100-call budget. The complete workflow failed, so case 7
does not pass acceptance. No read-policy denial occurred.

The original payload was restored, and the real healthy task sequence completed
successfully at 09:49:34 UTC before case 10 was prepared.

Case 7 workflow: `05206a87-c055-4fb6-b313-b1ce9eb7b98c`; execution:
`3bd575b3-00a8-415b-ac87-a3fb505a68e1`; agent run:
`110d9202-f987-49c4-9ccf-ab1f5301cef3`.

## Case 10 findings

The final report identifies the configured missing source directory, maps
`Packages$` to `C:\Packages`, and cites two direct local queries showing that
`C:\Packages\NP-TaskSequence-MissingSource` does not exist. It relates this
observation to the current DistMgr snapshot failure with Win32 error 2 and
SourceVersion 5. Existing CLIENT1 content version 4 is correctly kept separate;
the report does not invent a current client failure. The proposed remedy is to
restore the intended source or correct the package source to an existing,
readable directory. No repair is executed by the agents.

The first candidate still left absence versus service-account access unresolved.
The host completion check at event 272 triggered a focused source/share/ACL
follow-up and renewed reviews: 15 further model calls, 20 tool calls and four
delegations. This produced the direct object evidence needed to distinguish the
current missing directory from the broad wording of the original log message.
No case-specific host prompt was introduced. The final answer remains overly
cautious about an additional permission defect, which does not negate the
observed absent source.

One review-quality issue remains: the proposed post-repair success check insists
on retaining version 5. It should correlate the actual repaired/published
version rather than require the version number to remain unchanged. Restoration
in this specific run did finish with version 5 and State 0, so this issue did not
cause the reference validation to fail.

Case 10 workflow: `02042fc0-5bd0-459e-9d14-85b2a4d10b8c`; execution:
`d32d819c-790d-4a85-a6f7-652905609fb1`; agent run:
`1d414275-3223-4c14-9d4c-93fa494a6b3a`.

## Audit and restoration

Both runtime journals confirm Luna for every member. The verification script
asserts the exact model and checks both executions are terminal and both
workflows disabled. The active global model remains Luna.

All 32 PowerShell calls (12 in case 7, 20 in case 10) contain read/query/format
operations. AST inspection finds no dynamic invocation, method calls or parser
errors; the mutator scan finds no candidates. The remaining tools are file
reads/searches, skill-resource reads, run-memory operations and delegation.
There are no `tool_failed` events or permission denials in either run. Expected
missing-path shell results return exit code 1 and are inspected as evidence,
not reported as successful reads.

Neither CLIENT1 nor CM1 has MsiInstaller events or WindowsUpdateClient events
19/20/21 during either diagnostic execution window. These event checks support
the command audit; they are not a claim to detect every possible OS side effect.

After case 10, the original source was restored, distribution reached State 0,
and the healthy task sequence completed at 09:57:10 UTC. Cleanup verified the
original payload hash, source, cache quota and update configuration, restored
the original service state, removed deployment CHQ2000F and disabled the test
task sequence. Temporary WinRM firewall/access adapter and recovery tasks were
removed; TrustedHosts was restored to its saved baseline. The test package and
task sequence remain available for review, without an active deployment.

## Remaining acceptance work

- Make internal context compaction robust to a truncated summary response, then
  repeat case 7 exclusively with Luna. The 2,048-token working-summary cap and
  run-wide failure handling are product behavior; this failure cannot be
  attributed solely to Luna's diagnostic ability.
- Case 7 still lacks a completed causal diagnosis of the physical DP object.
  Its intermediate HTTP 401 explanation is not a passing result.
- General shared hypothesis/check state, clearer tool-error classification and
  more focused reviewer work remain follow-up implementation items. They were
  not deployed during this unchanged-backend comparison.
- Improve success-check scope/version reasoning without introducing rules for
  these particular SCCM faults.

This validation adds test-harness enforcement, an isolated case-10 fixture and
the report; it does not claim new runtime fixes. No commit, push or installed
Windows service restart was performed.
