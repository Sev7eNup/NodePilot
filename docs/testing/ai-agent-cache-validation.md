# Client cache diagnosis follow-up — 20/21 September 2026

Branch: `feature/ai-agent-activities`. Evidence and controller scripts are retained in
`.runlogs/agent-cache-validation/`. This report distinguishes runtime correctness
from the semantic accuracy of a model's diagnosis; an execution marked Succeeded
does not itself pass the diagnostic acceptance test.

## Changes

- Review evidence is shared across all team roles. A new observation from another
  reviewer can support the original objection owner's reassessment. It does not
  approve that objection automatically. Identical target/tool/input/result tuples
  from different roles do not count as new evidence; distinct targets do.
- Specialists now receive the latest other-member reports separately from the
  supervisor's assignment, as reviewers already did. Those reports remain untrusted.
- Empty optional filters in `evidence_list` no longer produce unknown-ID errors.
  Nonempty unknown IDs still fail, and cross-run access remains prohibited.
- General instructions distinguish wrapper/child execution and the inputs of each
  phase. They require comparable object/version/property/algorithm/scope, complete
  inventories, and separate observations from causal interpretation in handoffs.
  They do not equate a constituent digest with a whole-object digest.
- Windows diagnostic skill 1.0.21 documents the PkgLib → DataLib → FileLib mapping,
  physical file inspection, and comparison with the client inventory. This is
  [documented product structure](https://learn.microsoft.com/en-us/intune/configmgr/core/plan-design/hierarchy/the-content-library),
  not a classifier for a particular fault. No test filename, package ID, expected
  digest, injected fault or error-code-to-cause rule was added to product guidance.
- Large observation previews now show separately labelled beginning/end excerpts
  and their offsets. The middle remains available through evidence recall; neither
  the preview nor a recalled subset is presented as whole-source coverage.
- Short tool results now include their actual evidence ID as well. They previously
  required index lookup, which the live members sometimes replaced with guessed IDs.

No shell permission was broadened. No repair action was added or executed by agents.
Raw tool snapshots, context budgets, redaction and the 250 MB collection cap remain.

## Automated checks

The review-state regression first produced 2 failures and 9 passes. The empty-filter
and specialist-handoff regressions produced 3 failures and 1 pass before their fixes.
The beginning/end preview regression failed for both output limits before its fix.

After correction: **574 AI tests passed**, including real framework delegation,
cross-reviewer evidence, preserved objection ownership, observation deduplication,
cross-target distinction, specialist handoffs, optional filters, lossless recall and
bounded previews. **16 Engine skill tests passed**. The API build before the initial
live tests succeeded with 54 existing warnings and zero errors.
The short-observation ID regression also failed before its fix. The existing
tool-call-with-stop regression now checks the original text and its assigned ID
inside the evidence envelope rather than expecting an unwrapped string.

## Live protocol

The controller uses the isolated six-step task sequence CHQ0000B, content CHQ0000A
version 2 and a CLIENT1-only collection. The new temporary deployment is CHQ2000C.
The sequence performs harmless package/file and endpoint checks; it installs no
software or updates. Baseline and post-restoration task sequence runs must succeed.

The five-member team has a supervisor, client/server specialists and client/server
reviewers. All agent access is through NodePilot's checked, read-only tools over
real WinRM. Each attempt has a fresh workflow and member sessions. The neutral task
contains the task sequence, content/deployment identity and UTC start, but no fault
description. The controller's preparation, backup and oracle files are outside the
agents' allowed evidence paths.

Case 5 removes one test-package cache file; case 6 alters one cached manifest while
preserving its name and length. The controller restores original bytes after each
investigation and runs a fresh healthy reference. A scheduled recovery task provides
a backstop. A healthy blind control checks for inappropriate diagnosis from old logs.

## Intermediate results retained

All-Luna attempt 1 (skill 1.0.20) finished with 35 model / 84 tool calls / 4
delegations in 163.6 seconds. It repeated the aggregate hash symptom and wrongly
compared that with an individual-file digest. Both reviewers' evidence-list requests
failed because optional filters were empty. Strict result: **incomplete**.

All-Luna attempt 2 (skill 1.0.20, corrected handoffs and filters) finished with
64 model / 114 tool calls / 9 delegations. Reviewers raised further questions, but
the team still failed to isolate the missing file and hashed a metadata INI instead
of the physical content it describes. Strict result: **incomplete**.

All-Luna attempt 3 (skill 1.0.21) used 67 model / 96 tool calls / 10 delegations
in 263.5 seconds. It found the missing client file, read its source/DataLib/FileLib
representation and retained the correct observed difference. Its final answer still
failed to turn that into a sufficiently scoped current-cause diagnosis and targeted
client remedy. Strict result: **partial**, not a full pass.

All-Luna case 6 (skill 1.0.21) used 57 model / 117 tool calls / 8 delegations in
342.1 seconds. It reported the aggregate hash failure and did not isolate the
altered cached manifest. Strict result: **incomplete**.

## Model comparison

The active profile remains gpt-5.6-luna. The comparison workflow overrides only
supervisor and reviewer models to gpt-6-astra; both specialists remain on Luna.
Task, tools, skill version, runtime and budgets are otherwise unchanged. This is
an observed comparison, not a guarantee of model accuracy or an automatic model
escalation mechanism. The chosen model supports tool calls according to
[OpenAI's model documentation](https://developers.openai.com/api/docs/models/gpt-6-astra).
An isolated tool-free NodePilot smoke call confirmed account/runtime compatibility.

Case 5: **complete current-cause diagnosis and scoped remedy**, 85 model / 172 tool
calls / 8 delegations, 529.3 seconds. The team compared all three files by version,
relative path, size and full SHA256 against physical DP objects, corrected an old
log excerpt and truncated/mislabelled evidence, then independently reviewed the
missing required file. The final report explains that this file belongs to the
package consumed by native verification before the custom checker starts. It
proposes only managed renewal of the affected client entry, not server redistribution.
The initiating actor and a byte-for-byte historical staging snapshot remain unknown;
neither negates the demonstrated current blocker.

[Case 5 comparison workflow](http://localhost:5173/workflows/07b2a24c-2b8e-4079-b44f-f3e00e20524a)
— execution `ef63ac37-afbc-4ac3-b914-d8b836569316`.

Case 6: **complete current-cause diagnosis and scoped remedy**, 87 model / 170 tool
calls / 7 delegations, 543.8 seconds. The cached manifest alone differs from the
matching package source and physical DP content. Review corrected both an incorrect
version claim and the assumption that the custom checker had executed. The final
answer identifies native package verification as the failing phase and recommends
managed renewal of only the affected client cache entry. Original bytes were restored
and a fresh real task sequence succeeded afterward.

[Case 6 comparison workflow](http://localhost:5173/workflows/edcde88c-e715-4f5c-8e04-44dfbbfc92e9)
— execution `4fdec48c-2bfc-4686-acf6-6457a5a0d356`.

## Final-build follow-up

After these completed comparisons, the user limited subsequent runs to Luna, with
Terra as the maximum permitted model. No subsequent Sol or Astra calls were made.
The active profile remains Luna; subsequent fault workflows explicitly select
`gpt-5.6-luna` for every member. The final build also includes short-result evidence
IDs and bounded head/tail previews. A healthy blind control and the outstanding
cases 3, 5, 6, 7, 9 and 10 were repeated against this build. Results and cleanup
are recorded below; previous trials retain their original grades.

### Healthy blind control

The final-build all-Luna control passed: 33 model / 54 tool calls / 4 delegations,
136.2 seconds. Both reviewers confirmed the current successful run. The final report
correctly converted the explicit CMTrace bias, separated earlier hash failures from
the current attempt and required no current repair. It included a conditional
recommendation for a hypothetical recurrence; this was not an executed action or
an attribution of a current fault. Short-result evidence envelopes were observed in
the live journal.

[Healthy control workflow](http://localhost:5173/workflows/f5125179-fbb7-49bb-8897-4685bb476767)
— execution `e11608a9-5203-4216-af6e-76ea114fd1a6`.

### Case 3 — endpoint/port mismatch

All Luna: 46 model / 83 tool calls / 8 delegations, 234.4 seconds. **Current blocker
identified; remedy conditional on intended configuration.** The final report maps
the actual step-05 timeout to port 18530, verifies a successful complete listener
inventory without that port and IIS bindings on 8530/8531, and excludes the current
download/hash phase. The review correctly rejected a prior no-match query carrying
exit code 1; another reviewer supplied a clean observation which the objection owner
accepted. The report does not claim to know who changed the configuration. It leaves
the intended port unproven and offers the corresponding narrowly scoped alternatives.
Restoration was followed by a successful real reference run.

[Case 3 workflow](http://localhost:5173/workflows/bfb55798-bfca-4e1f-8763-e3e9e985b93f)
— execution `50a2af67-05ba-40b9-9c1f-527a8ea0080d`.

### Case 5 — missing cache file, final-build Luna

27 model / 63 tool calls / 6 delegations, 142.6 seconds. **Incomplete.** The report
correctly locates native content verification before the custom checker and proposes
a scoped, supported client-cache renewal. It does not identify the missing required
file as the concrete underlying difference. Server metadata/status are not a complete
physical inventory; reviewers accepted a symptom-level explanation too early. No
permission failure prevented the comparison: the only failed tool call attempted a
task-sequence directory which had already been removed. The original file was restored
and a healthy reference succeeded.

[Case 5 Luna workflow](http://localhost:5173/workflows/2e9558c5-e3d5-45d5-bdd6-faf8672ad17b)
— execution `e9d89892-1ec5-4d52-9e4e-f72299e80e6b`.

### Case 6 — altered cache manifest, final-build Luna

39 model / 81 tool calls / 6 delegations, 212.3 seconds. **Incomplete.** Reviewers
requested a full comparable inventory but subsequently accepted a symptom-level
answer without that investigation. The final report lists all three filenames and
the aggregate hash mismatch, yet does not isolate the altered manifest. The only
failed tool call was the absent task-sequence directory; no permission denial blocked
the needed cache/DP comparison. The report also overstates uncertainty about the
CMTrace bias despite available time context. Original bytes were restored and a
healthy reference succeeded.

[Case 6 Luna workflow](http://localhost:5173/workflows/f017eaec-25b7-4d65-bd2d-b76d76187c0e)
— execution `7d6eed71-8425-4f99-9448-5d359f067e3c`.

### Case 7 — missing physical DP content

40 model / 94 tool calls / 7 delegations, 384.8 seconds. **Incomplete.** The report
identifies the failed directory-list/download phase, IIS 404 on the HTTPS path and
401 on fallback, but never checks the missing physical FileLib object. It proposes
further IIS/path/authentication investigation instead of the concrete content remedy.
Restoring only the backed-up physical file, without changing authentication, was
followed by a successful fresh download and task sequence. One guessed log filename
was absent; no read-permission denial blocked the content inspection.

The controller initially stopped before injection because its ownership check
expected only content version 1. Inspection showed references from versions 1 and 2,
both exclusively belonging to the isolated test package. The guard now permits
multiple versions of that package and still rejects any foreign reference. No foreign
package content was modified. The initial attempt launched no agent workflow.

[Case 7 Luna workflow](http://localhost:5173/workflows/fccd3dec-a5e5-48b0-a734-50717e97810d)
— execution `5ea5faa2-f4de-44f2-8b45-7c8edd0cf608`.

### Case 9 — corrupted physical DP content

29 model / 74 tool calls / 4 delegations, 135.3 seconds. **Incomplete.** The team
locates the aggregate integrity failure, but hashes metadata/source files rather
than completing the physical FileLib comparison. Both reviewers accept remaining
DP-versus-transfer uncertainty. Two guessed paths were absent; there was no relevant
permission denial. This remains a reasoning/investigation gap, not a budget failure.

The physical byte was restored and its original digest verified. The first reference
collector captured only a 221-byte post-rotation finalization line, with no success
marker, and stopped the suite. A subsequent read found all three cache-file digests
already correct; this does **not** establish residual cache corruption. The controller
renewed only this test package's cache entry and performed another real reference,
which succeeded at 23:08:57 UTC. The ambiguous first capture is retained.

[Case 9 Luna workflow](http://localhost:5173/workflows/0a095e1f-5dcb-497a-b234-01f8b70feb8f)
— execution `f9a1b690-6d45-4ede-b55c-0877e1837ece`.

### Case 10 — incorrect package source

46 model / 120 tool calls / 9 delegations, 269.7 seconds. **Partial.** Review rejects
the specialist's initial use of an older successful run. The final report identifies
the exact unusable UNC source, current DistMgr failure and empty client DP-location
reply, and correctly avoids inventing a completed task-sequence failure while content
is unavailable. It still does not independently distinguish an absent directory from
service-account access failure. A provider query returned `0x80041001`; that is not
a NodePilot permission rejection. No direct UNC-read denial occurred in this attempt.
The header also abbreviates the supplied site code incorrectly as `CH` instead of
`CHQ`; this is a report accuracy defect, not a changed lab setting.

[Case 10 Luna workflow](http://localhost:5173/workflows/cbdd150b-ca27-4fc1-b298-42710ecbe53d)
— execution `95b05da4-962e-4cc0-b9ef-79a1760b3b76`.

Restoration returned the original source and DP state 0. The initial client reference
remained in the earlier content request and produced no fresh task-sequence log. A
controller-triggered machine policy retrieval allowed the pending request to use
content version 3; the real task sequence then succeeded at 23:20:38 UTC. No agent
triggered that policy retrieval. Luna traces and oracle snapshots are archived under
`attempt5` before the two cache cases are repeated with Terra. The active profile
remains Luna; only those two comparison workflows override their member models.

### Case 5 — Terra comparison

All five members use Terra: 51 model / 118 tool calls / 4 delegations, 309.0 seconds.
**Complete current-cause diagnosis and scoped remedy.** The final report explicitly
identifies the missing client file, the incomplete local cache and native hash failure
before child-process execution. The server specialist reads the three DataLib records,
hashes all three actual FileLib objects and all three source files, and compares the
complete version-3 inventory with the two client entries. Reviewers independently
confirm the missing entry and correct the distinction between cache reuse and a fresh
transfer. Historical initiating actor remains unproven; the current blocker does not.
The proposed remedy renews only the affected cache content. Restoration and a real
healthy reference succeeded.

This is a repeated blind investigation, not a perfectly controlled model benchmark:
the package is now version 3 after case 10, with unchanged test-file bytes and more
historical log entries. Task wording, tools, skill, runtime and budgets are unchanged.

[Case 5 Terra workflow](http://localhost:5173/workflows/7cc90ee7-6dd6-4f95-8805-cdcb14c497af)
— execution `40007c0e-758c-4a83-ab32-d92ee5f3bf4e`.

### Case 6 — Terra comparison

All five members use Terra: 57 model / 118 tool calls / 4 delegations, 292.4 seconds.
**Complete current-cache diagnosis and scoped remedy.** The final report identifies
the 64-zero local manifest, compares it with the source manifest and payload hash,
and connects the actually reused cache instance to native content verification before
the child checker. It proposes renewal of only that cache instance. No tool call
failed. The report preserves the limit that the historical DP transfer is not proven;
unlike the case-5 comparison, it does not complete physical FileLib hash verification.
It therefore does not establish the historical origin of the local defect. Original
manifest bytes were restored and a real reference succeeded at 23:31:45 UTC.

[Case 6 Terra workflow](http://localhost:5173/workflows/2d1f5825-ee4b-4271-a37c-21af0c1abafa)
— execution `256a4714-d242-4e14-b5db-cd455208bf6a`.

## Latest diagnostic acceptance

| Original case | Final-build Luna | Terra follow-up | Remaining gap |
|---|---|---|---|
| 3 — WSUS port | Current endpoint/listener mismatch identified | Not repeated | Intended port remains conditional; no historical actor identified |
| 5 — missing cache file | Incomplete; symptom and targeted remedy only | Current missing-file/cache cause identified | Luna does not reliably perform the full comparison |
| 6 — altered cache manifest | Incomplete; concrete file not isolated | Current manifest/cache cause identified | Luna detail diagnosis; historical transfer origin not proven |
| 7 — missing DP object | Incomplete; HTTP symptoms only | Not repeated | Physical FileLib absence and corresponding remedy |
| 9 — corrupted DP object | Incomplete; aggregate hash symptom only | Not repeated | Physical FileLib byte/hash mismatch and corresponding remedy |
| 10 — incorrect source | Partial; exact source and content block identified | Not repeated | Missing directory versus service access; precise version correlation |

The healthy Luna control passed. These are observed runs, not a guarantee that a model
will solve every equivalent incident. No fault-specific rules, expected filenames,
digests or oracle data were added to product instructions. None of the incomplete
Luna cases exhausted the model-call budget. Reviewers sometimes accepted a statement
of uncertainty instead of completing an available investigation; general runtime
review gates do not provide semantic correctness. Earlier grades remain preserved.

## Final execution and read-only verification

All 15 diagnostic executions recorded in this report are terminal and their workflows
are disabled. Nine occurred after the user's model restriction: seven all-Luna runs
(healthy control plus six faults) and two all-Terra cache comparisons. Their journals
confirm only permitted models. The active global profile is still `gpt-5.6-luna`.

The combined journal audit contains 313 PowerShell calls. Command syntax inspection
found read/query and formatting commands, no invoked member methods, and no modifying
command, `Win32_Product`, MSI invocation or policy/scan/repair trigger. All 30 machine
execution windows (15 on CLIENT1, 15 on CM1) contain zero MsiInstaller events and zero
WindowsUpdateClient installation events 19/20/21. This is evidence for these runs;
controller fault setup, SCCM execution, policy refresh and restoration are separate
authorized operations. Audit artifacts are `command-audit.json`, `event-audit.json`
and `run-verification.json` under the report's run directory.

## Restored final state

At 23:33 UTC the controller verified original cache quota (20,480 MiB), WUServer on
port 8530, BITS/wuauserv Manual/Stopped and CcmExec Automatic/Running. CM1 has the
original source, original physical payload digest and successful distribution state 0
at version 3. Both WSUS and MP probes return HTTP 200. No recovery task or fault
firewall rule remains. Deployment CHQ2000C was removed and the test task sequence
disabled; reviewable test assets remain. The temporary CLIENT1 adapter/firewall were
removed and host TrustedHosts restored to `localhost`.

The dev API remains on `feature/ai-agent-activities`; API readiness and the designer
both return HTTP 200. No installed-service restart, commit or push was performed.
Detailed restoration records are `cleanup-client.json`, `cleanup-server.json` and
`cleanup-host.json`. The final API build succeeded with zero errors and 54 existing
warnings; the recorded automated verification remains 574 AI and 16 Engine skill tests.

The user-requested all-Terra repeats of cases 7, 9 and 10 on 21 September are
recorded separately in [Terra server fault validation](ai-agent-terra-server-validation.md).
They preserve these earlier results and compare the unchanged product/skill build.
