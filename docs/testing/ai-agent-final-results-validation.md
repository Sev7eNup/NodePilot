# Agent final reports and task outcomes — 21 September 2026

Branch: `feature/ai-agent-activities`. This follow-up implements complete final
reports, separate task assessments, narrower review invalidation and retained
preliminary findings. It does **not** establish full feature acceptance.

## Changes

- A separate tool-free final model call synthesizes the initial report, latest
  update, member findings and investigation states into a self-contained report.
  One call is reserved inside the existing budget. User JSON schema validation
  remains separate from the host's assessment envelope; one format correction is
  allowed within the remaining budget.
- `outcome` and `outcomeReason` are available in step parameters, the run API and
  history. `completed`, `partial` and `blocked` describe the model's task assessment;
  technical success does not imply complete investigation. Missing assessments
  remain `unassessed`. Host-recorded blocked checks prevent `completed`.
- Identical specialist findings, owner reassignment and reordered evidence IDs
  no longer invalidate reviews. New observations and material finding changes
  still do. Argument validation identifies the field and violated constraint
  without echoing rejected values.
- The first draft is persisted before finalization. Failure or cancellation
  retains it as explicitly preliminary, possibly superseded findings. No model
  call or tool retry is made after a timeout to manufacture a final answer.

## Deterministic verification

| Scope | Result |
|---|---|
| Complete AI test project | 604 passed |
| Engine agent tests and frontend catalog synchronization | 305 passed |
| Runner tests after adding final-model timeout coverage | 10 passed |
| API agents, dependency direction, settings synchronization | 12 passed |
| UI trace/projection unit tests | 8 passed |
| Initial browser editor/history validation | 4 passed |
| Final browser final-report/interrupted-draft variants | 2 passed |
| MCP / CLI agent tests | 2 / 2 passed |
| Final conclusion/runtime tests after instruction refinement | 50 passed |
| Additional transport assertion: final instructions reach ILlmClient | 1 passed |

These overlapping runs must not be added into a unique test total. The final UI
build and TypeScript compilation passed. API build passed with existing warnings.
Browser tests use mocked APIs; backend integration tests exercise real SQLite.
The deadline test collection now runs without other collections in parallel to
avoid competing-load failures in its one-second deadlines; assertions and limits
were not weakened. Full repository release CI was not run.

## Actual-model validation

All members used **gpt-5.6-luna**. Sources were temporary loopback HTTP fixtures with
random item identities. No SCCM/Windows changes were made. Four distinct scenarios
were exercised; the incomplete-source scenario also had a repeat.

| Scenario | Model/tool/delegation calls | Duration | Result |
|---|---:|---:|---|
| Four-member team, one absent item in complete source | 32 / 38 / 5 | 113.3 s | Passed: exact absent key, complete JSON report, `completed` |
| Four-member team, matching sources in different order | 18 / 16 / 3 | 73.4 s | Passed: no invented differences, complete JSON report, `completed` |
| Single agent, unavailable tools/sources | 3 / 1 / 0 | 14.1 s | Passed: limitations retained, `blocked` |
| Single agent, explicitly incomplete source | 3 / 2 / 0 | 18.7 s | **Failed assessment:** reports limitations but claims `completed` instead of `partial` |
| Same incomplete-source scenario after generic instruction refinement | 4 / 4 / 0 | 20.7 s | **Still failed assessment:** `completed`; remedies remain too definite for uncertain absence |

Every successful technical run produced a complete schema-valid report, with no
tool calls after `report_finalizing`. Event sequences were continuous; API outcome
and step parameters matched, as did the API report and step output. Actual model
IDs were checked in run context. These protocol successes do not make the two
incorrect semantic assessments pass.

An earlier incomplete-source attempt was rejected before creating an AgentRun:
the harness requested 50 calls while the configured single-agent ceiling was 20.
The harness was corrected to 20 model / 40 tool calls; administrator limits were
not changed. This attempt is retained separately, not counted as a model test.

Execution IDs, in table order:

- `4f3e2f01-204e-4d32-9317-f12ba779312f`
- `ee7e3c2f-38c1-4913-bc82-661e51defda8`
- `2ab2d7b3-52b7-4348-b54b-4dc6a557b2e7`
- `b9625458-403f-40fa-bf81-5594f533d447`
- `771b6a46-742b-4412-ae46-c2176371e948`

Rejected harness execution: `3390026c-d942-4e2f-8e9a-27044a21fb3e`.
Artifacts: `.runlogs/agent-conclusion-validation/`,
`.runlogs/agent-conclusion-single-validation/`, and
`.runlogs/agent-conclusion-coverage-validation/`. Final verification is recorded
in the first directory's `verification.json`. All six workflows are disabled and
all executions terminal. Temporary HTTP fixture servers stopped. The installed
NodePilot service was untouched; the rebuilt dev API remains available.

## Remaining acceptance work

### Terra comparison requested after the Luna probes

One additional run used `gpt-5.6-terra` as an explicit per-agent override, with
the same task wording, schema, incomplete-source construction, host code and
20-model/40-tool budgets. Random item IDs and loopback endpoint changed as in the
previous repetitions. Execution: `06a24a4a-9930-4e6e-97a7-b9f528ae926a`;
run: `6dc9de9b-6786-467d-994f-37d8885ac022`.

**Passed:** technical `Succeeded`, semantic `partial`, 3 model calls, 2 HTTP tool
calls, 23.3 seconds. Terra explicitly distinguishes absence from the supplied list
from absence in the actual inventory and says a complete comparison is not
established. The final report matches the user schema and finalization uses no
tools. Its remedy is more conditional, although the unknown cause is still not
resolved and the proposed publication correction must not be treated as proven.

This is evidence of model-dependent assessment on this scenario, not proof that
the host is free of all defects or that Terra succeeds consistently. The two Luna
assessment failures remain recorded. The test workflow is terminal/disabled;
global model settings were never changed, and subsequent tests remain on Luna.
Artifacts: `.runlogs/agent-conclusion-terra-validation/`.

### Still open

1. **Conservative completion assessment.** Luna can acknowledge material missing
   evidence and still claim completion. Prompt refinement alone did not resolve
   this. Host checks only cover explicitly registered blocked checks; they cannot
   infer arbitrary source completeness. No fixture-specific rule was introduced.
   A next general improvement is explicit task-coverage obligations, each with
   supporting evidence or an unresolved limitation, evaluated before completion.
2. **Remedies under uncertainty.** Missing observations must not become definite
   underlying absence. Proposed changes must remain conditional until evidence
   distinguishes collection failure from an actual missing object.
3. **Efficiency and repeatability.** Review invalidation is corrected, but these
   runs do not prove a general cost/latency reduction. The first team still used
   32 model calls. Long cross-source investigations and upstream model timeouts
   still need repeatable end-to-end evaluation.
4. **Release acceptance.** Production-domain SSO, external MCP-provider integration
   and full release CI remain open. Prior remote-skill and large-log tests were
   not repeated here. No claim is made that previous SCCM cases 5, 6 or 9 now pass
   with Luna because of these changes.
