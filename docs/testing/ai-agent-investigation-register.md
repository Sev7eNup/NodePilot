# Shared investigation register — 21 September 2026

Branch: `feature/ai-agent-activities`. Agents remain read-only. All actual-model
tests in this follow-up explicitly use `gpt-5.6-luna` for every member.

## Implementation

Teams receive two internal working-memory tools, `investigation_read` and
`investigation_update`. Up to 20 focused checks record stable ID, question, owner,
hypothesis, original evidence IDs, counterevidence IDs, next check, status,
conclusion and limitation. Owner selection is restricted to configured members;
evidence references must resolve to originals from the same run. Entries are
redacted and journaled before publication. They are not fresh tool observations.

Each delegation receives the current index, with full entries available on demand.
Open entries block completion alongside existing member/reviewer requirements.
Changes invalidate reviews. Closed entries must be explicitly reopened before
revision; unchanged updates are idempotent. This prevents editorial approval notes
from repeatedly invalidating completed reviews. The existing journal/history/export
shows updates with a German/English event label. No extra target permissions,
database table, external diagnostic tool or SCCM-specific outcome rule was added.

Diagnostic output instructions require findings, causal evidence, counterevidence,
uncertainty, scoped proposed remedy, verification and remaining checks. Existing
user-defined JSON schemas and non-diagnostic output formats remain authoritative.
The host validates check structure/references, not factual correctness or whether
every necessary question was registered. A model can still incorrectly close or
block a check; independent review remains necessary.

## Deterministic verification

- An unresolved-check runtime test failed before integration and passes afterward.
- A closed-check rewrite regression failed before the explicit-reopen rule and
  passes afterward. Real handoff, citation validation, no fake evidence, reopening,
  idempotency, journal failure and redaction are covered.
- 588 AI tests passed on the corrected sources.
- 301 Engine agent tests passed on repetition. One total-deadline variant failed
  during the initial parallel load; all six cancellation/deadline variants passed
  separately and the entire group then passed. No thresholds were weakened.
- Eight selected API/controller/architecture/settings tests passed.
- Eight frontend trace/projection tests, TypeScript compilation, UI build and four
  designer/history browser tests passed. Browser tests use mocked API fixtures.
- Dev API rebuilt successfully; 54 existing warnings, no errors. Installed service
  was untouched.

A subsequent bare-output provenance regression failed before correction:
`evidence_read` returned a hash/value but omitted its stored original invocation.
It now includes a bounded invocation preview and permits paged `view=input` reads
of the original redacted arguments. The invocation identifies the requested object,
not whether the operation succeeded. No new remote read is needed. The full AI
suite passed 589 tests after this change. An additional escaping/budget boundary
test then exposed oversized preview metadata at a 1,024-character limit; the
preview is now shortened independently and all 19 context tests pass. This final
preview-only bound correction was made after the verified live series started;
it was built into the dev API after those runs finished. The final incremental
API build passed with zero warnings/errors.
The full AI suite was then rerun with the boundary correction: 590 passed.

## Initial live iteration retained

Artifacts: `.runlogs/agent-investigation-board/`.

The first generic missing-item comparison found the correct difference but failed
completion after 49 model calls: the supervisor repeatedly rewrote the reviewed
entry, making its review stale. A differing-value comparison was deliberately
cancelled after the same pattern appeared, before deploying the correction. This
is not counted as a diagnostic pass. Both workflows are terminal and disabled.

The initial SCCM case 5 completed technically (95 model / 144 tool / 11 delegation
calls, 449.4 seconds) but did not establish the missing-cache-file cause. Original
fault bytes were restored and the reference task sequence succeeded at 12:03:12 UTC.
The next injection was stopped before starting, to deploy the correction.

## Intermediate corrected iteration

Artifacts: `.runlogs/agent-investigation-final-1/` and
`.runlogs/agent-investigation-final-2/`. Case 5 used all 100 model calls and still
did not isolate the cache-file cause. Repeated source-path objections exposed the
provenance omission above. Its reference passed at 12:12:51 UTC. An already-started
repeat was deliberately cancelled for the correction; restoration and its healthy
reference passed at 12:13:55 UTC.

The generic suite completed six attempts: missing-item and healthy cases each
passed twice. Both quantity-difference diagnoses
identified the exact item and values, but included explanatory prose in the
`differentKeys` strings, which the exact-key oracle rejected. The schema then only
required strings, without an exact-key pattern; those are not missed diagnoses.
The next full series explicitly describes and validates the key-only fields.

## Verified live protocol

Historical baseline: Terra previously identified the concrete cause and scoped
remedy for [cache cases 5 and 6](ai-agent-cache-validation.md#case-5--terra-comparison)
and, after the read/budget improvements, for [DP case 9](ai-agent-read-budget-validation.md#blind-comparison).
An earlier [Luna case-9 repeat](ai-agent-luna-569-validation.md#case-9) also identified
the faulty object and scoped remedy, with historical-delivery linkage qualified.
Those successes remain valid. The tables below describe only the new Luna series;
they do not mean these cases have never been solved. Different code revisions,
package versions and accumulated logs prevent attribution of all differences to
the model alone or a controlled estimate of the new register's effect.

Artifacts: `.runlogs/agent-investigation-verified-1/` and
`.runlogs/agent-investigation-verified-2/`.

Two sequential repetitions of cases 5, 6 and 9 use fresh five-member teams and the
unchanged neutral SCCM task. The controller alone injects/restores the isolated
fault, verifies it remains present through diagnosis, and requires a healthy
reference after each case. Budgets remain 100 models / 500 tools / 20 delegations.

Separately, six four-member Luna comparisons use randomized batch/item identifiers
and HTTP-served complete expected/observed inventories: missing item, changed
quantity and identical reordered data, twice each. The task does not disclose the
expected difference. Their results use a JSON schema for findings, differing/missing
keys, evidence, uncertainty, remedy, verification and open checks. These probes run
concurrently with the SCCM series, so durations are not controlled performance
comparisons with earlier runs.

All six generic comparisons passed their exact-key oracle and completed successfully:

| Comparison | Repetition | Model calls | Tool calls | Seconds |
|---|---:|---:|---:|---:|
| Missing item | 1 | 35 | 37 | 109.8 |
| Changed quantity | 1 | 27 | 30 | 94.6 |
| Healthy, reordered inventory | 1 | 21 | 23 | 66.2 |
| Missing item | 2 | 20 | 22 | 74.9 |
| Changed quantity | 2 | 22 | 20 | 67.3 |
| Healthy, reordered inventory | 2 | 25 | 23 | 77.3 |

The check covers the missing/different keys, not semantic correctness of every
sentence. All 14 generic attempts across the retained iterations were separately
verified terminal, their workflows disabled, and all four members configured with
Luna. Evidence: `agent-investigation-board/general-verification.json`.

### SCCM verified repetition 1

| Case | Runtime | Models / tools / delegations | Diagnostic assessment |
|---|---|---|---|
| 5: missing client cache file | Failed | 99 / 135 / 20 | Missing `required.txt` not established; final required reviews remained incomplete. |
| 6: changed client manifest | Succeeded | 51 / 95 / 7 | Hash symptom and failing step established; changed manifest not isolated. |
| 9: damaged DP payload | Succeeded | 60 / 115 / 7 | Content-library descriptors found, but damaged physical payload not established. |

None is counted as a fully solved causal diagnosis. The case 9 report asserted a
FileLib read limitation without an attempted physical payload hash read being
denied. Its two `Get-FileHash` requests targeted the package source, not the DP
blob. A root directory listing and metadata inspection do not establish that
nested payload files cannot be read. The report also gave undue weight to a
historical response as a requirement for current byte comparison.

Further friction: model-authored register conclusions sometimes exceeded schema
length bounds; the existing generic validation error does not identify the field.
The explicit reopen guard prevented closed-note edits but some agents still tried
them repeatedly. These are visible costs, not evidence that the model alone is
responsible or that the implementation is fully optimized.

The controller verified each fault persisted until diagnosis ended, restored it,
and obtained successful real task-sequence references at 12:25:53, 12:31:46 and
12:37:09 UTC. Model IDs and disabled/terminal workflows were checked against the API.

### SCCM verified repetition 2

Case 5 completed technically with 85 model / 150 tool / 12 delegation calls in
488.6 seconds, but again missed the missing cache file. The client specialist
listed two cache files, while the server specialist listed three expected files.
The team verified the matching payload hash without completing the set comparison.
Its last answer summarized review approval instead of providing a self-contained
full diagnosis, and mentioned nonexistent shared-check IDs. The optional JSON
template was not configured for these unchanged text-output SCCM workflows; its
presence does not enforce a result contract on arbitrary text output.
The restored reference passed at 12:45:50 UTC.

Case 6 completed with 79 model / 135 tool / 11 delegation calls in 448.0 seconds.
This time the client comparison identified `manifest.sha256` precisely: 64 zero
characters and a file hash different from the source, while payload and required
file hashes matched. The final report preserved this correct finding. It still
treated the link to the failed package validation as only plausible and deferred
the remedy to additional DP/hash-semantics investigation. Assessment: concrete
fault identified, but cause/remedy closure remains partial. It is not equivalent
to the first repetition, which never isolated the manifest.
The restored task sequence passed at 12:53:49 UTC. A `files_list` request for
`C:\SMSPKGSIG` was correctly blocked by the configured file-tool path policy;
required source/cache paths remained permitted.

Case 9 failed at the model budget: 100 model / 149 tool / 12 delegation calls,
573.5 seconds. The server reviewer initially required a complete comparison and
the host rejected closing that review without fresh evidence. Follow-up still
compared source bytes and descriptors, not the physical FileLib payload. The
damaged DP file was not identified. The restored reference passed at 13:04:01 UTC.

| Case | Repetition 1 | Repetition 2 |
|---|---|---|
| 5 | No concrete missing-file diagnosis; runtime failed | Missing file overlooked despite two-vs-three inventories; runtime succeeded |
| 6 | Hash symptom only; runtime succeeded | Changed manifest correctly identified, causal/remedy closure partial; runtime succeeded |
| 9 | DP corruption not isolated; runtime succeeded | DP corruption not isolated; model budget exhausted |

These results do not demonstrate improved SCCM pass rate or establish that the
remaining failures are solely a model limitation. They show functioning shared
state, evidence provenance and host gates, alongside remaining comparison,
review-efficiency and final-report weaknesses. No fault-specific solution was
added to prompts or skills during this work.

## Safety and cleanup

Across all nine retained SCCM attempts (three intermediate, six verified), 158
agent PowerShell invocations were reviewed using command extraction and PowerShell
AST parsing. No write commands, dynamic invocations or method calls were present;
no `Win32_Product` queries were used. CLIENT1 and CM1 had zero MsiInstaller or
WindowsUpdateClient installation events (19/20/21) in these execution windows.
This is evidence for these runs, not a universal proof that every possible read
operation is side-effect-free. Fault injection/restoration and task-sequence starts
were controller operations, not agent permissions.

All six verified SCCM workflows are terminal and disabled, with all five members
using Luna. All 4,068 persisted events across the six SCCM and six generic final
comparisons have continuous sequences; register revision sequences are continuous
and attributed to configured members.

At 13:04 UTC cleanup restored the DP blob's original SHA-256, package distribution
State=0/SourceVersion=5, client cache quota 20,480 MB and original service startup/
running states. MP and WSUS endpoints returned HTTP 200. Deployment CHQ20012 was
removed, CHQ0000B disabled, temporary test adapter/firewall removed and TrustedHosts
restored to `localhost`. Test package/collection/disabled task sequence remain for
inspection. The installed NodePilot service was not restarted or changed.

## Final deployed-build smoke test

The dev API was restarted on the final compiled source and readiness returned 200.
One additional healthy four-member Luna comparison was attempted afterward.
Specialists and reviewer found the correct identical inventory, but the supervisor's
final model call timed out after the 180-second per-call limit. The run failed
explicitly without retry: 21 model calls, 23 tools, three delegations, run
`2f34971c-60d3-402e-876b-3c3abc6ba6fb`. It is **not** a successful smoke test and is
separate from the six passed comparisons above. No cause beyond the observed lack
of a complete model response within the limit has been established.

The temporary fixture server stopped, and this workflow is terminal/disabled too.
The final generic verification therefore covers 15 attempts including intermediate
failures/cancellation and this timeout, all with Luna. Dev readiness still returns
200; installed service PID 9436 remained running unchanged. Artifact directory:
`.runlogs/agent-investigation-final-smoke/`.

## Remaining work

Follow-up implementation and validation of final reports, task assessments and
review invalidation: [final-results validation](ai-agent-final-results-validation.md).
That report includes an unresolved actual-model completion-assessment failure;
it does not supersede the historical results below with a blanket pass.

- Reliable complete cross-source comparisons in longer investigations, including
  distinguishing metadata from the actual bytes and noticing absent objects.
- Reviewer acceptance tied to the original causal objective; avoiding repeated
  symptom confirmation or unsupported claims that a permitted read is unavailable.
- Lower register/review overhead, field-specific schema errors, and a complete
  final report after review instead of a review-only delta.
- Repeatable end-to-end success under real model latency. The final timeout stopped
  correctly, but its upstream cause was not diagnosed in this change.
- Production-domain SSO, external MCP integration and full release CI remain
  outside this targeted validation; the real local-account folder-RBAC probe passed.

## Real authorization probe

On the actual dev API/database, a completed comparison workflow was temporarily
moved into a dedicated folder. Two temporary Viewer accounts had their default
root grants removed; only one received a FolderViewer grant. The authorized account
received HTTP 200 for runs and events, including investigation updates. The account
without the grant received 404 for both endpoints. The workflow was returned to
its original folder and test users/folder were removed through their APIs.
Evidence: `agent-investigation-final-1/rbac-result.json`.

This covers local account/folder authorization. It does not establish production
domain-account SSO, external MCP-provider behavior or a full repository release CI.
