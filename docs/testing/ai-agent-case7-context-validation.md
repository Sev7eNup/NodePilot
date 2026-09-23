# Case 7 context recovery and blind validation — 21 September 2026

Follow-up to [the Luna comparison](ai-agent-luna-investigation-validation.md).
Branch: `feature/ai-agent-activities`; artifacts:
`.runlogs/agent-case7-context-validation/`.

## Reproduced failure and changes

The prior case-7 run stopped at model call 54 while compacting the client
reviewer's history. Its tool-free summary client had a separate 2,048-token
output limit. The normal agent's 250,000-token ceiling did not apply to this
internal call. Two regression tests reproduced rejection of a truncated working
summary and the absence of bounded recovery before the fix.

Working requests now allow up to 8,192 output tokens, still bounded by the
profile/host ceiling. A truncated context summary is discarded and requested
once more with a shorter-note instruction from the same retained source exchanges.
The additional call consumes the existing model/time budget and reserves the
final-answer call. Partial notes and partial tool calls are never accepted.
Repeated truncation stops the current compaction without losing its source chunk;
normal truncated agent responses remain fatal and do not execute tools.

Existing `evidence_read` accepts an optional literal case-insensitive query,
returning a bounded original passage with absolute offsets. A third test
reproduced the missing targeted recall and now verifies both a late match and
the scope of a negative search. This is run-memory recall, not a new remote
tool, permission or fresh external observation.

General instructions distinguish failed fallback branches from the actual
remaining blocker and distinguish source metadata from a derived/served object's
state. Reviewers focus on decisive claims, alternatives and targeted original
passages. No SCCM error code, FileLib path, expected missing filename or test-case
answer is encoded in runtime instructions.

## Automated verification

All 582 AI tests pass on the final source, including context budget/failure,
tool-pair preservation, evidence recall, and rejection of truncated agent tool
calls. API build succeeds with zero errors and 54 existing warnings. Only the
verified Dev API was restarted; readiness returned HTTP 200. The installed
NodePilot Windows service was not restarted.

## Live validation

All five test members are pinned to Luna; the harness rejects other
models. The test retains the neutral original assignment and 100/500/20 budgets.
Old result artifacts are unchanged. Preparation, restoration and harmless
reference task-sequence execution remain controller actions; the agents stay
read-only.

The healthy baseline completed at 10:29:36 UTC. CLIENT1 initially had not yet
received deployment CHQ20010; the controller waited for policy availability before
the successful reference and fault injection.

### First follow-up: compaction fixed, diagnosis still incorrect

Workflow `1461e4f3-882c-48c1-a106-e8c24c9c2a1c`, execution
`82ead9be-0b7c-468e-8fde-042e539e5879`, run
`a3b9bca9-fcf1-4764-ad56-3382588172f0` completed technically with
82 model calls, 143 tool calls, 13 delegations in 518.3 seconds.

Three live compactions succeeded: 191,444 to 61,220 characters, 188,886 to
98,037 and 194,042 to 135,724. They retained 78, 102 and 112 evidence references
respectively. None required the new truncated-summary retry; that branch is
covered by the deterministic regression tests. Targeted evidence recall was
used 12 times. One read failed because the active SMSTS log had moved; there
were no permission denials. All 48 PowerShell calls were read operations;
neither machine recorded MSI or update-installation events during the run.

The team eventually traced the endpoint through IIS and package metadata to the
correct physical storage directory. However, original observation ev-00111
contained only a 73-byte companion metadata file and a 220,560-byte signature.
The expected 25,165,824-byte payload itself was absent. The specialist, reviewers
and final report incorrectly treated those companions as evidence of payload
presence. The technical `Succeeded` status therefore does **not** mean diagnostic
acceptance passed. The report also overqualified the current attempt's time
correlation.

The payload was restored and the healthy reference succeeded at 10:40:46 UTC.

### Second follow-up: exact object identity

General evidence/reviewer instructions now require literal comparison of the
claimed and observed full object identity, type and size. Metadata, index and
signature objects cannot substitute for referenced data. A decisive uncertain
identity requires an exact-object read. There are no product-specific names,
extensions or error codes in this instruction. The final 582-test AI suite
passes; the second API build has zero errors/warnings.

The second blind run uses the same neutral task and deployment, with artifacts in
`.runlogs/agent-case7-object-validation/`.

**Result: cause and scoped remedy identified in the completed final report.**
The team traces CHQ0000A version 5 through PkgLib and the per-file DataLib record
to the exact FileLib payload. It distinguishes the 73-byte metadata companion
and 220,560-byte signature from the expected 25,165,824-byte data file. A direct
`Get-Item` of the full expected path returns `PathNotFound`. The report correlates
this with payload-specific IIS HTTP 404 responses while directory/manifest
requests succeed. The HTTP 401 fallback is kept separate from the demonstrated
HTTPS payload failure. A successful launcher exit is no longer treated as a
successful task-sequence completion.

The proposed remedy is supported ConfigMgr redistribution/correction of the
affected package on its DP, with exact payload/download and task-sequence
verification afterwards. The agents propose this without executing it and do
not recommend manually editing the content library or a broad client/IIS repair.
The historical actor responsible for the missing file remains appropriately unknown.

| Run | Diagnostic acceptance | Model calls | Tool calls | Delegations | Seconds |
|---|---|---:|---:|---:|---:|
| Original Luna comparison | Failed at summary truncation | 54 | 77 | 5 | 238.4 |
| First follow-up | Technical completion; wrong payload-presence inference | 82 | 143 | 13 | 518.3 |
| Second follow-up | Missing payload and targeted remedy identified | 66 | 177 | 10 | 532.4 |

Second workflow: `8a609373-d5a8-497c-8847-e06ba9b07af2`; execution:
`40173629-e92f-47e4-8082-7727ebee1ab3`; agent run:
`85926129-6198-4f39-b98d-f89afe91066e`.

Targeted recall was used 32 times and two compactions completed. After the first
candidate answer, the host completion check led to 14 model calls, 22 tool calls
and three delegations, including the exact-path read and renewed reviews. The
reviewers had already identified the missing data object before that additional
check; it strengthened the direct proof rather than originating the entire cause.
There were no failed tools or permission denials in the second run.

This is a successful blind acceptance of this fault on the final diagnosis
instructions, not a measured general success rate. Logs evolve during each live
run, and model output is nondeterministic; changes to wording alone cannot be
isolated as the sole cause of the improved result. No expected fault was supplied
in the workflow assignment, and no test-specific rule was added to the product.

## Final audit and restoration

The second run's 19 PowerShell calls were read/query/format operations. Combined
with the first follow-up, all 67 PowerShell calls have no mutator candidates,
dynamic commands, method invocations or parse errors. Neither CLIENT1 nor CM1
recorded MsiInstaller or WindowsUpdateClient 19/20/21 events during either
diagnostic execution window. These scoped audits are not a blanket proof about
every possible OS event.

Both executions are terminal and both diagnostic workflows disabled. Both run
contexts and the active profile are exclusively Luna. The injected payload
absence was checked after each diagnosis and before controller restoration.
The final restored task sequence succeeded at 10:53:21 UTC.

Cleanup completed at 10:53:54 UTC: original payload hash/source restored,
distribution State 0, WSUS/MP HTTP 200, original service/cache/update settings,
deployment removed and test task sequence disabled. Temporary access adapter,
firewall rule and recovery task are removed; TrustedHosts is back to localhost.
The installed Windows service remains unchanged. Dev API is rebuilt/running;
no commit or push was performed.

A final journal-counter correction counts both summary attempts when the bounded
retry is used. The 17 context tests pass after that bookkeeping change, including
the exact retry count. The live runs did not exercise the retry branch; its
discard/no-tool-execution behavior is covered by deterministic tests.

Shared structured hypothesis management and broader tool-error classification
remain separate feature follow-ups. They are not claimed as implemented here.
