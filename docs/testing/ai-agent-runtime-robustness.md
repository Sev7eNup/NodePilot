# Agent runtime robustness: completion, outcomes and tool feedback

Date: 2026-09-22. Branch: `feature/ai-agent-activities`.
Scope: the three non-diagnostic follow-ups to the pinned-skill SCCM round.
No SCCM-specific rule, additional permission or diagnostic answer was added.

## Changes

- Investigation updates patch supplied fields. Existing owners, questions and
  evidence survive a focused update; new checks default to the caller and open
  status. Resolution still requires original evidence. Closed edits return
  `accepted=false`, unchanged current content and actionable reopening guidance.
  They neither invalidate reviews nor pretend the proposed change was saved.
- Final synthesis must enumerate requested substantive deliverables in `coverage`,
  with fulfilled/unresolved status and a supporting basis. The host downgrades
  completion with unresolved items, never upgrades an explicit blocked judgment,
  and still checks blocked investigation entries. Coverage is journal metadata;
  the user's text/JSON schema stays unchanged.
- Final synthesis receives actual tool attempt/success/failure counts and bounded
  last-call input/result excerpts. This helps correct unsupported action claims
  in working reports without discarding a supported successful result. Excerpts
  are explicitly bounded; they do not describe every earlier invocation. Source
  contents remain untrusted and confer no permissions.
- Schema feedback names missing fields, maximum text/item limits and empty strings,
  without echoing rejected values. General failures distinguish invalid arguments,
  permission denial and execution errors; existing process-specific errors remain.
  Empty HTTP arguments now fail schema validation. Invalid Get-WinEvent parameter
  combinations explain that LogName belongs inside FilterHashtable.

## Automated reproduction and verification

Four focused assertions reproduced the missing patch behavior, missing required-field
and limit diagnostics, and absent per-requirement assessment contract before fixes.
Regression tests exercise real runtime correction (only the corrected read executes),
the host activity ledger, outcome persistence with an unchanged user JSON schema,
closed-check immutability/reopening, HTTP schema validation and event-query correction
while a mutating event command remains prohibited.

Final targeted tests: 116 AI/agent/adapter/team tests and 347 Engine agent tests pass.
The development API builds with zero errors (54 existing warnings in the earlier
build; the final incremental build reports zero warnings). This is
not a complete repository release CI or a new SCCM diagnostic acceptance round.

## Live validation

All configured members use `gpt-5.6-luna`. The fixtures serve random-key inventories
over local read-only HTTP, use normal published workflows and retain full journals.
No source changes or global model/agent-limit changes are made by the tests.

Initial corrected runs: incomplete-source probes return partial twice (3/2 and 5/4
model/tool calls). Four-member healthy and missing-item teams return completed with
correct results, three delegations each and zero tool failures (21/19 and 19/24).
Each team attempts one closed-check rewrite, receives the unchanged check and
finishes without reopening or forcing another reviewer cycle.

Two further defects were retained and corrected during validation:

1. A no-source run returned partial by counting generic future advice and report
   formatting as fulfillment. Coverage instructions now distinguish substantive
   deliverables from these constraints and explanations; explicit blocked judgments
   cannot be upgraded. Two subsequent no-source probes return blocked (3/1 each).
2. A deliberate empty-HTTP-argument probe first reached permission validation as
   empty strings. The schema now rejects these. Subsequent Luna calls directly
   used valid arguments, so the planned invalid call did not occur. A working
   report nevertheless claimed an earlier rejection. The final activity ledger
   exposes that contradiction. This probe must not be counted as a successful
   live schema-error/retry exercise when no rejection actually occurred; the
   deterministic runtime test covers that path.

Final live suite (all Luna, all assertions passed):

| Scenario | Task outcome | Model/tool calls | Delegations | Duration | Execution ID |
|---|---|---:|---:|---:|---|
| Sources unavailable | blocked | 3/1 | 0 | 19.4 s | `88b56106-edc1-4689-9bd1-1ca44b1fcb7a` |
| Incomplete source | partial | 4/2 | 0 | 70.3 s | `e0678bdf-52b3-4ca5-954a-5598d7d8e8a9` |
| Complete matching inventories, four-member team | completed | 21/21 | 3 | 81.7 s | `103a57f1-2f7c-4e97-82a8-89a5bfa6cfe7` |
| Missing entry, four-member team | completed | 19/18 | 3 | 84.8 s | `61078eee-19f5-4b08-a955-455be6b89fc5` |

All four final syntheses are tool-free. The missing-entry report identifies the
exact missing key without asserting an unproven cause. Technical execution success
is separate from the task outcomes above.

The final deliberate invalid-call probe (`6b60e901-68ca-4d87-9989-0e76fc1da4b0`)
again made only a valid GET. Its final report now accurately records HTTP 200 and
explicitly does not claim an earlier rejected call; outcome is partial. Report
integrity passed, but the intended live schema-retry path remains **unexercised**;
its original probe result remains false. Automated runtime tests cover that path.

Cleanup audit verified 18 distinct test workflows: all disabled, all executions
terminal, all members configured with Luna, all journals contiguous. Earlier
failed and unexercised probes remain in `.runlogs/agent-robustness/`; they are not
silently replaced by later successful results. No CM1/CLIENT1 changes were made.
The final event-query argument-error classification was verified by the 347 Engine
tests after this live suite and included in the final development API rebuild.

## Limits

Coverage and its basis remain model-authored. Structural enforcement cannot prove
that all user requirements were identified or that a claimed fulfillment is true.
Likewise, tool counters are execution facts, not proof of diagnostic correctness.
The small neutral team runs do not establish a universal cost reduction or resolve
every long investigation's review/budget behavior. Read-only gates and configured
target/credential boundaries are unchanged.
