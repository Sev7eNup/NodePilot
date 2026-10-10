# Diagnosis quality follow-up — 2026-09-20

Scope: open item 3, evidence interpretation and structured diagnosis consistency.
This continues [the original acceptance](ai-agent-diagnosis-acceptance.md) and
[host-enforced team review](ai-agent-review-acceptance.md).

## Reproduction and change

The preserved original run misclassified a stopped/disabled service as a generic
configuration mismatch. A fresh unchanged five-case baseline passed 2/5 strict
comparisons: categories and booleans were correct, but cases 02–04 appended line
or byte annotations to source filename fields. These remain failed comparisons;
the oracle was not relaxed to strip annotations.

Inspection found that only the supervisor received the final result schema and
category definitions. Delegated members could investigate and review without
knowing those distinctions. The runtime now supplies the original team task and
final schema to members while retaining their status/content delegation protocol.
A regression test failed before the change and passed afterward.

General evidence instructions now require matching operation/endpoint/target when
weighing later success against earlier failure, proper treatment of transient TCP
snapshots, successful same-scope cross-checks for ambiguous empty queries, and
exact source identifiers and contiguous quotes. Uncertain historical timezones
remain uncertain. These are generic rules, with no case IDs, expected answers or
Windows-specific categories embedded in the runtime.

The fixture schema explicitly defines source filename and quote fields. Expected
categories and booleans for the original five cases are unchanged. Five additional
fixtures exercise recovery, unrelated success, transient connections, empty-query
cross-checks and unknown historical timezone. The local runner now loads the
checked-in schema directly and enables the technical reviewer function for teams.
Consequently this is acceptance of the combined runtime/contract/review changes,
not an isolated statistical estimate of a single prompt's effect.

The first expanded run passed **9/10** strict cases. Case 10 correctly stated that
the unknown historical timezone prevents ordering failure and success, but still
selected `no_current_fault_proven` rather than `undetermined` (execution
`8cf90759-7aa5-4aa0-ae19-8a153e5e08d1`). The shared instruction's success rule was
too broad: it now explicitly requires success to be established as the latest
relevant outcome. The schema describes unresolved failure/success ordering as
undetermined; the expected answer is unchanged. The complete suite is rerun after
this correction, rather than counting the failed run as accepted.

That rerun corrected case 10 but exposed overapplication in case 04: the reviewer
treated the successful comparison on port 443 as conflicting with failure on port
8443 and demanded chronology to classify an already demonstrated mismatch. Its
needs_input was followed up and reviewed again, but the final classification was
still wrong (`860873b0-e51b-4661-ae9d-0ba2f9bb4da9`). The ordering rule now expressly
requires conflicting outcomes for the same operation/endpoint/target. Missing
metadata only limits conclusions that depend on it.

The same batch reported a case 07 quote failure caused by the evaluator, not the
model: fixture strings used LF but Python wrote CRLF files on Windows. Both quoted
CRLF excerpts exactly matched the actual source bytes. The evaluator now compares
decoded actual file bytes, without trimming or normalizing the returned quote.
Original run records are preserved. The batch had one real diagnosis failure and
one evaluator false positive; it is not reported as a clean acceptance run.

## Method and boundaries

Actual model executions use the dev API and persisted workflow/event infrastructure.
Only `files_list` and `files_read`, restricted to the corresponding synthetic case
directory, are available. The expected answers stay in the controller; agents see
the question, neutral observations and output schema. Each result is checked for
category, all four booleans, exact source filename and verbatim source substring,
plus nonempty assessment/next check. Narratives are inspected separately.

Source fixture hashes must remain unchanged. Workflows are disabled and the
temporary service-identity setting restored after each batch. No CLIENT1 fault,
remote command, repair or new WinRM collection is performed by these tests.

API build: zero warnings/errors. Runtime/configuration/completion tests: **29 passed**.
The installed NodePilot service is unchanged; the dev instance runs the new build.

Correct semantic reasoning remains model-dependent. JSON validation and the review
completion gate do not prove a diagnosis true. These small synthetic tests do not
replace remaining real SCCM/WinRM acceptance or establish an error-rate guarantee.

## Final actual-model results

The complete final batch passed **10/10** strict comparisons. All workflows also
completed successfully. The earlier failed batches above remain part of the record.

| Case | Distinction | Result | Execution ID | Seconds |
|---|---|---|---|---:|
| 01 | Missing projected field versus installation damage | Pass | `97389990-472b-4ac0-b7b8-ce1794438f5d` | 9.4 |
| 02 | Disabled service, unknown actor | Pass | `a53f80d5-969d-4341-b19c-80ce03924e3d` | 40.1 |
| 03 | Denied/truncated checks versus system corruption | Pass | `767fc52b-4f23-4b5f-8ed2-dfadb7db9171` | 30.1 |
| 04 | Current port mismatch versus historical database failure | Pass | `86e2b78d-3b3e-4950-b467-0f7f15302b3f` | 32.4 |
| 05 | Proven absent required executable and failed extraction | Pass | `029cab08-9178-4933-ad22-40303c5808aa` | 7.7 |
| 06 | Later same-scope success, explicit differing UTC offsets | Pass | `315c13e8-2297-485d-bdc3-d89a75f5f8c6` | 30.8 |
| 07 | Later unrelated success does not repair report endpoint | Pass | `485043ac-e437-4cd5-a523-5dd767757ea8` | 29.6 |
| 08 | Empty TCP snapshot between successful scans | Pass | `b8e70cbf-e20c-4fe1-bb6e-050e0f1e6c1f` | 7.8 |
| 09 | Failed filtered query, successful full same-scope cross-check | Pass | `00ad83a8-5c10-490c-8b8e-8e7d9963e920` | 30.2 |
| 10 | Unknown historical timezone prevents ordering failure/success | Pass | `244fcd2f-dd75-4ca5-a9e5-d02ff9c778d7` | 8.8 |

Narrative inspection confirmed the distinctions, not just enum agreement. Case 04
identifies the wrong port and proposes the specific correction plus end-to-end
verification, explicitly not executed. Case 06 rejects a repair based solely on
the superseded historical error. Case 09 keeps upstream filtering outside its
local-rule conclusion. Case 10 retains unknown chronology without borrowing the
target's current UTC setting. None claims whole-system health or a known actor.

Local reproducibility artifacts: `.runlogs/agent-live/diagnosis_probe.py`,
`diagnosis-quality-verified-20260920-summary.json` and matching per-case result/event
files. The checked-in inputs are `fixtures/agent-diagnosis-cases.json` and
`fixtures/agent-diagnosis-result-schema.json`. Earlier batches use prefixes
`diagnosis-baseline-20260920`, `diagnosis-quality-20260920` and
`diagnosis-quality-final-20260920`. Passwords/tokens are not part of these artifacts.

Final cleanup completed: all ten generated workflows disabled, service-identity
setting restored, fixture hashes unchanged. The development instance remains
available with the corrected branch build. This closes the bounded synthetic
diagnosis-quality work; real-machine acceptance remains a separate release task.
