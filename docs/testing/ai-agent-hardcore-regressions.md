# Agent diagnostic progress regressions

Branch: `fix/agent-diagnostic-progress`. Baseline: live CM1/CLIENT1/GW1 tests of
2026-10-07, NodePilot 1.4.5, gpt-6-luna, SCCM skill 1.0.0, Power mode enabled.
Five fresh native failures were injected before symptom-only agent prompts.

| Case | Baseline | Required regression result |
|---|---|---|
| Missing required cache file | Passed | Identify exact missing file, compare full source/DP inventory, scoped cache remedy |
| Cache manifest replaced with zero digest | Passed | Isolate manifest mismatch from healthy payload/source/DP |
| Physical DP FileLib payload corruption | Cause found, controller stopped review loop | Verify physical blob hash and source identity; finish reviewed report without repeating completed investigations |
| Active application detection uses wrong WOW6432Node path | Failed; outer 31-minute timeout | Retrieve exact active DeploymentType rule; compare hive/path/view/name/type/value with installed marker |
| Update signer absent from TrustedPublisher | Partial | Correlate exact update/CAB, signer and trust stores; separate established blocker from unresolved publisher authorization |

Machine-readable replay definitions and native counterfactual checks are in
[the five-case matrix](fixtures/agent-hardcore-five.json). These are skill-assisted
regression cases, not unseen holdouts. Keep expected conditions/controller evidence
outside agent-readable paths. Do not equate execution success with a passed diagnosis.

## Repeat on an isolated lab

1. Record build, model/profile, skill version/hash, Power mode and outer workflow
   timeout. Bind existing read-only diagnostic members to the three lab machines.
2. Establish native healthy behavior. Snapshot exact bytes/settings and arrange
   restoration before injecting one fault. Use only owned test assets; a FileLib blob
   must belong exclusively to the test package. Do not touch shared package content.
3. Inject the matrix's single change, reproduce the fresh native failure and capture
   the UTC start/end interval. Pass only `prompt` and that interval to the workflow.
4. Wait for terminal execution and export complete events, report and task outcome.
   Grade every listed requirement against original observations. Record timeouts,
   controller cancellation and partial outcomes as such; no final report means no pass.
5. Restore in a finally/recovery path, run the native counterfactual, remove owned
   assets, and verify services/VM state before the next case. Never install the
   publisher test update merely to validate its download/trust behavior.

The original CLIENT1 Windows evaluation expired and caused two shutdowns; this is
an environmental confounder to resolve before a fresh live acceptance campaign.
The first missing-file execution had an invalid controller time window and was
excluded; the replacement execution is the baseline pass.

## Deterministic runtime coverage

- `AgentContextTests.SourceTruncationRemainsVisibleOutsideTheEvidenceExcerpt`:
  process truncation remains visible outside a shortened envelope (failed before fix).
- `AgentRuntimeTests.PowerModeStopsRepeatedReadsAndProducesPartialReport`: real
  framework tool loop, unlimited budget, repeated observation, warning, tool stop,
  final synthesis and enforced partial outcome.
- `AgentProgressWatchTests`: cycling old states cannot reset the watch; genuinely
  new observation signatures permit sustained investigation.
- `AgentRuntimeTests.ReusesCurrentReviewOnlyForTheSameSubmission`: identical current
  reviews reuse approval; changed submissions invoke the reviewer again. Existing
  review dependency tests cover invalidation by changed sources.
- Existing context-compaction tests preserve original evidence/skill guidance during
  sustained reads of distinct observations.
- `AgentActivityRunnerTests.InterruptedJournalRetainsMemberFindingAndOpenChecks`:
  database reload after cancellation preserves findings and pending work without
  inventing final assessment metadata.
- `AgentResultSummary.test.tsx`: execution status is separate from partial outcome;
  interrupted results are labeled intermediate.

These tests use controlled model responses to verify runtime behavior. They do not
establish that a live model diagnoses all five cases correctly. No new live VM
campaign or deployment is implied by passing them.

Validation on 2026-10-08: 164 AI agent tests, 367 Engine agent tests and 23 UI
summary/trace tests passed. TypeScript build checking and ESLint for the changed
components passed. The compaction assertion and final checkpoint/package tests
were also rerun after the final edits. Existing unrelated compiler/analyzer warnings
remain; no dependency or database migration was added.

## Live retest on CM1, 2026-10-08 (Europe/Berlin)

The signed artifact `1.4.5-agentfix.20261008` was built from the uncommitted branch
and installed on CM1 after a verified COPY_ONLY database backup. Installed API,
Ai and Engine SHA256 hashes match the published build. Assembly informational
version still identifies the base commit; the artifact label and component hashes
identify this test build. Workflow version 6 pins SCCM skill 1.0.1 for all six members.
Model remains gpt-6-luna, Power mode enabled, outer timeout 1,860 seconds.

| Case | Before | Retest | Model / tool calls / delegations |
|---|---|---|---|
| Physical DP payload | Cause found, review loop; controller cancelled at 13:46 | **Passed**, normal completed report in **3:54** | 81 / 173 / 5 |
| Wrong active detection path | Cause missed; 31-minute timeout | **Passed**, normal completed report in **5:52** | 119 / 186 / 6 |

DP execution: `476709fb-e385-46ca-890b-7cc47e055df1`.
The team measured the exact physical FileLib payload hash, compared source and
descriptor, identified the DP corruption and proposed supported scoped redistribution.
Both reviewers finished once. After byte restoration and fresh download, the same
native task sequence passed.

Detection execution: `4b0cdf49-aafb-491f-b58e-7a5467fb2263`.
The team queried exact Application `/3` and DeploymentType `/2` identities, read the
actual WOW6432Node rule, verified the expected key was absent, and read the installer
plus actual native 64-bit marker. It identified the path mismatch and proposed the
specific detection correction. One context compaction occurred; the run still finished
normally. Restoring the rule produced Application revision 4, Installed, ErrorCode 0.

Running intermediate reports were captured in both executions. Neither progress
warning/stop nor cached-review reuse was needed, so these live runs do not separately
exercise those guards or interruption persistence; deterministic tests cover them.
No source-truncation event was needed in the narrowed detection queries. This is one
skill-assisted repeat per case, not a statistical guarantee or isolated attribution
to each individual runtime change.

The complete event sequences are contiguous (658 DP / 757 application). Requested
native commands contained no controller/recovery-path references or mutating commands.
Each run corrected three tool argument/policy errors without losing its final result.
CLIENT1 was deliberately rebooted before fault injection; its expired evaluation was
not changed. No unexpected client restart occurred during either measured run.
Owned application/deployment/collection/source/registry/cache assets were removed,
DP content restored, and CM1, GW1 and CLIENT1 were running at the final check. NodePilot,
MP and WSUS health checks passed. The existing task-sequence deployment was preserved.

Artifacts: `.runlogs/cm1-agent-fix-retest-20261008/` contains complete events,
reports, source/build hashes, native controls, command audit and restoration checks;
the result bundle is also retained on CM1 under
`C:\NP-Release\agentfix-20261008\results`. The live validation used the uncommitted
working tree; no push was performed.
