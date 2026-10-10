# Agent diagnostic evidence acceptance — 2026-09-19

## Scope and correction

Historical report: follow-up implementation and results are recorded in
[diagnosis quality](ai-agent-diagnosis-quality.md). The separate review-protocol
gap described below is addressed in [review acceptance](ai-agent-review-acceptance.md).
The original failures remain preserved here.

The original CLIENT1 run inferred installation damage from an absent projected
registry `ClientVersion` value. Independent inspection found a registry
`ProductVersion` and a responsive `root/ccm:SMS_Client.ClientVersion`. The reviewer
repeated the weak inference. See [the original report](ai-agent-winrm-acceptance.md).

Shared agent instructions now require distinguishing observations, hypotheses,
demonstrated causes and proposed checks. They cover failed/truncated queries,
missing properties, counterevidence, historical versus current failures, current
blockers versus attribution, and consistency of structured results with their
definitions and explanation. These instructions apply to both activities and all
team members, without requiring the Windows skill.

The Windows example skill adds `resources/diagnostic-evidence.md`, concrete
read-only version queries and cautions against treating a working version query
as full client health. Existing evaluation results may be read; client evaluation
and log-conversion actions that can remediate or write are not diagnostic read
operations. No shell/tool permission code was changed in this correction.

This is model guidance, not a deterministic causal inference or proof engine.
It cannot guarantee that every model response is correct.

## Test method

Five synthetic cases use the actual configured model through the dev API and
workflow engine. Two use an individual agent, three use a supervisor, analyst and
reviewer. Specialists actually read the files; the supervisor has delegation only.
Only `files_list` and `files_read` are selected, restricted to each case directory.
No PowerShell, remote connection, HTTP, MCP or modifying tool is selected.

[Fixtures](fixtures/agent-diagnosis-cases.json) separate model-readable `files`
from controller-only `expected` answers. Only the files are copied into allowed
directories. The question and [result schema](fixtures/agent-diagnosis-result-schema.json)
are supplied to the agent. The schema defines categories, not case answers.

The controller compares the category and four booleans with expected answers,
requires an explanation and next check, and verifies each literal evidence quote
against its named fixture. File hashes must remain unchanged. These checks do not
prove every sentence is semantically entailed; results are also inspected manually.

The live runner is retained locally at `.runlogs/agent-live/diagnosis_probe.py`,
with its existing `live_probe.py` API helpers. Final run prefix:
`diagnosis-verified`. Generated workflows, execution journals and JSON evidence
remain available for inspection. Fixture workflows are disabled after the run;
the temporarily enabled dev service-identity setting is restored.

## Iteration history

The first run passed four of five strict comparisons. In the denied-access case,
the narrative rejected WMI corruption but marked the mechanism as proven because
it interpreted the field as the cause of the failed query. The field definition
was clarified to refer to the investigated system problem. Expected outcomes were
not relaxed. Editorial explanations were also removed from fixtures so subsequent
runs had to reason from observations rather than repeat supplied interpretations.

The next neutral-fixture run passed three of five: the access-denial boolean was
still wrong, and the disabled service received `configuration_mismatch` instead of
`service_unavailable`. The final revision adds a general consistency instruction
and defines the otherwise overlapping category labels in the test schema. Earlier
failed runs are retained; they are not reclassified as successes.

## Final actual-model results

All five workflows executed successfully, but only **four of five** passed the
strict diagnostic-result comparison. Engine success is not diagnostic acceptance.

| Case | Expected distinction | Result | Execution ID | Seconds |
|---|---|---|---|---:|
| 01, individual | Missing projected version is not installation damage; partial checks do not establish full health | Pass | `5e12695a-3b15-4d8b-8d33-3a9ab3bf4556` | 10.0 |
| 02, team | Stopped/disabled service is the current blocker; actor and installation damage remain unproven | **Fail: category** | `8904a717-cfaa-4a4a-b6a8-97a456c52c5a` | 47.2 |
| 03, team | Denied WMI/registry queries and truncated search do not establish corruption or removal | Pass | `79a56183-fece-450a-a596-4e8f46fec8cb` | 41.3 |
| 04, team | Current port mismatch explains failed connection; old database error does not | Pass | `28704ccb-8623-4cc7-a143-c853dbb7b15b` | 51.7 |
| 05, individual | Verified required executable is actually absent and extraction failed: incomplete installation is supported | Pass | `637c8658-b64a-4ea9-a933-318025b8744e` | 11.1 |

Case 02 returned `configuration_mismatch` rather than the schema-defined
`service_unavailable`. Its narrative correctly identified stopped/disabled CcmExec
as the blocker, retained unknown attribution, rejected the old DNS error as proof
and did not infer installation damage or whole-system health. All four booleans
and all literal evidence checks passed. The label mismatch remains a failure,
despite the useful diagnosis; stricter category adherence is not accepted yet.

Case 03 now explicitly separates the proven query access failure from the unknown
mechanism of the investigated system problem. No new run established deterministic
reliability: these are individual stochastic model observations, not a measured
error-rate guarantee. All fixture hashes remained unchanged. Cleanup completed:
generated workflows disabled and service-identity setting restored.

## Automated checks and deployment

- API build completed without errors.
- Agent runtime/configuration tests: 19 passed after the final prompt change.
- Engine permission/skill tests: 70 passed; these implementations were unchanged.
- No prompt-string-only tests were added as a substitute for actual model checks.
- Dev API runs the branch build. Installed NodePilot service remains untouched.
- Example skill `windows-diagnostics` version `1.0.2` is available in the dev
  registry (ID `d790fab4-43e2-42c5-bd8c-83f9ae64fc87`). Existing workflow skill
  selections and immutable older versions are not changed automatically.

## Remaining acceptance scope

These are actual-model tests over synthetic evidence, not a new CLIENT1 blind
WinRM investigation. They do not test fresh remote collection or execution of the
new skill resource. The original query/value distinction is represented, but a
complete real-machine diagnostic rerun remains necessary.

Mechanical enforcement that a supervisor follows up every material reviewer
`needs_input` remains a separate open item. Independent corroboration is requested
by instructions; multiple agents reading the same evidence is not automatically
independent corroboration. The broader release checks in the original acceptance
reports remain applicable.
