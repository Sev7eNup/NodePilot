# Luna blind repeats of cases 5, 6 and 9 — 21 September 2026

Follow-up to [case 7 context/object validation](ai-agent-case7-context-validation.md).
Branch: `feature/ai-agent-activities`. Artifacts:
`.runlogs/agent-luna-569-validation/`.

## Method

Sequential blind runs on the unchanged final case-7 backend: case 5 (missing
client-cache file), case 6 (modified cached manifest), case 9 (modified physical
DP payload). Each case gets a fresh workflow/run and five fresh member sessions,
all pinned to `gpt-5.6-luna`. The harness rejects any other model. The neutral task,
skill version and 100 model / 500 tool / 20 delegation / 30-minute budgets remain
unchanged. No expected diagnosis, injected-fault description, original hash or
controller recovery artifact is supplied to the team.

Faults affect only isolated test package CHQ0000A and the CLIENT1-only harmless
task sequence CHQ0000B (deployment CHQ20011). The controller owns fault creation,
task-sequence triggering and restoration; agents retain read-only permissions.
Original bytes are saved before modification. A timed recovery task is installed
before each fault. The controller verifies the fault both before and after the
diagnosis, restores it, and requires a healthy reference before the next case.
Case 9 clears only the isolated package's cache before the test and after
restoration to ensure a fresh DP download.

Acceptance requires the concrete current faulty object and causal mechanism plus
a scoped remedy, not just a generic hash/download error or a technically successful
workflow. Previously successful Terra results are historical comparisons, not
substitutes for these Luna results. Historical logs and package version have evolved;
these repeats are not a controlled estimate of model success probability.

## Results

The healthy baseline succeeded at 11:03:08 UTC. No production code changes are
made during the comparison.

| Case | Diagnostic result | Model / tool / delegation calls | Seconds |
|---|---|---:|---:|
| 5 — missing cached file | Not passed: hash symptom, missing-file cause not identified | 57 / 87 / 10 | 279.4 |
| 6 — modified cached manifest | Not passed: hash symptom, changed manifest not identified | 41 / 87 / 8 | 240.9 |
| 9 — modified physical DP payload | Faulty object and scoped remedy identified; exact historical delivery linkage remains qualified | 50 / 99 / 9 | 317.4 |

### Case 5

The final report identifies native ConfigMgr content-hash validation failure and
correctly withdraws an earlier invalid comparison of a per-file SHA256 with the
aggregate package hash. It does not identify the removed `required.txt` in the
client cache. The client had already observed two cached files (ev-00021), while
the server inventory contained all three source files (ev-00028), but the team
did not complete the matching file-inventory comparison. Instead it leaves
source/DP/transfer/cache unresolved and proposes more investigation before any
specific repair. This does not meet causal acceptance, despite technical workflow
status `Succeeded` and 43 unused model calls.

Three tool errors: the absent temporary `C:\_SMSTaskSequence` directory and two
attempts to use `files_search` on directories. None is a permission denial. The
fault remained present after diagnosis. Restoration was followed by a successful
reference task sequence at 11:08:47 UTC before case 6.

Workflow: `6b540a52-aa67-4942-b082-0cb6c77a8dc5`; execution:
`a4315b96-0683-44f5-8425-9f3ab8c1bfcc`; run:
`d087fde2-9823-4b15-85df-77112ed543f8`.

### Case 6

The team correctly identifies the native aggregate content-hash mismatch, then
compares the actual `payload.bin` on client and source: sizes and SHA256 match.
However, it never establishes the changed cached `manifest.sha256` as the cause.
The final report leaves the deeper cause unresolved and recommends supported DP
validation/redistribution and a fresh client download without locating the faulty
cache object. It calls the discrepancy historical even though the injected fault
remained present throughout diagnosis. Companion files also belong to the package;
correctly distinguishing their hashes from the aggregate hash does not justify
omitting their own byte/content comparison. There were 59 unused model calls.

The only failed tool call concerned the absent temporary task-sequence directory;
there was no permission denial. The controller restored the manifest and the
reference task sequence succeeded at 11:13:17 UTC before case 9.

Workflow: `c28c726a-6c4f-4ace-8fe4-a8d4b7dd729f`; execution:
`f9337d8e-257d-4736-97fa-61c890f9c635`; run:
`577b8e9f-e4ae-4aac-aecd-cde709844732`.

### Case 9

After reviewer requests, the team traces package version 5 through PkgLib and
DataLib to the complete physical FileLib payload. It checks the actual 25,165,824-byte
object, not its INI/SIG companions. Descriptor SHA256 starts `3AAD819E`, while
the physical payload SHA256 starts `E23878D9`; these are the expected and injected
values independently known to the controller. It identifies this specific DP
inconsistency and proposes supported redistribution of only the affected package
on CM1, followed by hash verification and a fresh task-sequence run.

The final report qualifies this as a proven current blocker and plausible cause
of the observed client failure. It does not claim conclusive historical delivery
linkage: its IIS investigation finds no matching request in the selected log,
whose mapping to the actual DP application was not established. The corruption
and appropriate remedy are identified, but the strict end-to-end causal acceptance
is not fully met. Further correlation must use the actual DP site/log and client
request path, without treating successful HTTP transfer as proof of correct bytes.
The final success check also unnecessarily fixes package version 5; supported
content updating may legitimately advance that version.

Three failed reads concern two absent temporary task-sequence paths and absent
`despooler.log`; none is a permission denial. The fault remained present after
diagnosis. The controller restored the original payload, forced a fresh download
of only the test package, and obtained a successful reference at 11:19:11 UTC.

Workflow: `5ee21401-29a1-49de-a8cd-5bab10abdd18`; execution:
`4b20b7f7-c1db-44a7-9982-927ce50ea7bd`; run:
`2081859b-3082-40eb-9963-36b96499186d`.

## Audit and restoration

All three executions terminated successfully at the runtime level and their
workflows are disabled. Stored run contexts verify five Luna members per run;
the active profile also remains `gpt-5.6-luna`. No model failure, timeout,
context compaction or output truncation occurred. Seven failed tool calls are
the missing-path/directory-misuse errors described above, not read restrictions.

The 38 PowerShell invocations contain only inspection/formatting commands.
The mutation-pattern audit found no candidates; the AST audit found no method
invocations, dynamic commands or parse errors. Other calls are file/evidence/skill
reads and team delegation. In each diagnostic time window, CLIENT1 and CM1 have
zero MsiInstaller events and zero WindowsUpdateClient events 19/20/21. This is
evidence for these runs, not a universal proof that every possible command is safe.

Cleanup completed at 11:19:34 UTC. Test deployment CHQ20011 was removed and the
task sequence disabled. Recovery tasks are gone, the original DP hash is restored,
DP state is 0, and MP/WSUS endpoints return HTTP 200. Client cache quota, update
URL and original BITS/wuauserv service configuration are restored. Temporary
access adapter/firewall are removed; TrustedHosts is back to `localhost`.
The installed NodePilot service (PID 9436) was untouched; dev readiness returns 200.

Remaining diagnostic gaps: complete matching inventories and content comparisons
for **all** package files on client/source/DP (cases 5/6), and precise delivery-path
correlation (case 9). Available budget alone did not make the teams perform the
missing comparisons. These results do not isolate model ability from orchestration
quality and do not justify claiming all tested scenarios are solved with Luna.
