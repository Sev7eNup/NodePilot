# Parallel team delegation — validation, 2026-10-06

Tested against the local development API on port 5000 and UI on port 5173.
All live model calls used the active `gpt-6-luna` profile; the small comparison
explicitly selected that model for every member. No production deployment.

## Plan review

Implemented bounded assignment batches, shared-state synchronization, a gated
step-scoped database context, ephemeral peer pointers, settings, trace projection,
documentation and the generated test-suite case. See [ADR 0017](../adr/0017-parallel-team-delegation.md).

Adjustments to the proposed plan:

- Reserve the entire batch's tool/delegation budget atomically, rather than charging
  the wrapper first. Invalid or unaffordable batches start nobody and charge nothing.
- Reserve reviewer attempts only after budget admission. Record the reviewed
  revision so a concurrent material change cannot silently inherit approval.
- Reject batches whose minimum result metadata cannot fit the output limit; shorten
  individual content while retaining valid JSON and every result slot.
- Treat a shorter live wall time as a measurement, not a guaranteed acceptance
  criterion. Deterministic barrier tests and journal ordering prove concurrency.

## Automated checks

All scoped checks passed; no full test-suite or coverage run was performed.

| Check | Passed |
| --- | ---: |
| AI agent, completion, delegation and prompt-catalog tests | 144 |
| Engine agents, activity catalog/configuration and suite coverage | 366 |
| API agents and settings schema/frontend guards | 13 |
| Documentation counts | 28 |
| Trace and team projection Vitest cases | 10 |
| Agent trace and administration Playwright cases | 5 |

TypeScript checking and the final API build passed. The API was restarted from the
workspace build; both localhost listeners and `/healthz/ready` (200) were checked.
`git diff --check` passed. Existing staged merge content was left untouched.

## Live comparison: two specialists

Workflow: `[Agent Check] parallel suite smoke`,
`6f8d6f19-a599-47cd-9289-5a788f4301c6`. This reproduces the new suite case's
two independent confirmations, no selected host tools and JSON schema requiring
`{"answer":"OK"}`. It was created in the designer because extension file upload
was unavailable. The workflow was restored to limit 2 and left disabled after testing.

| Mode | Execution | Agent run | Result | Team duration |
| --- | --- | --- | --- | ---: |
| Parallel, limit 2 | `f7100fd6-89f5-483e-ba5c-14175b2421c4` | `a27fedc6-ce23-439d-b019-5d661471ff46` | Succeeded, completed, `{"answer":"OK"}` | 11.27 s |
| Sequential, limit 1 | `b56c2a99` | `f2a3119e-edbe-496e-bf52-22bac498b483` | Succeeded, completed, `{"answer":"OK"}` | 11.07 s |

Both used six model calls, two tool calls and two delegations. In the parallel run,
specialist starts are sequences 7 and 8; the first completion is sequence 12.
The starts are 0.756 ms apart. This small sample shows overlap, without a measured
wall-time advantage. It is not a throughput benchmark.

After the final result-envelope bound adjustment and API rebuild, the parallel
case passed again: execution `95472c4a-98cb-4276-a280-4d395e6156b0`, agent run
`10cb424b-a5d7-4739-9ad0-b95c17db0a80`, 8.22 seconds, five model calls, two tool calls,
two delegations and `{"answer":"OK"}`. Its exported journal is also contiguous.

## Live diagnostic workflow: 180 days

Workflow: `[Agent Check] luna smoke`, `90c21115-5d98-494f-ab10-79fcb352f1e4`.
Published with `maxParallelMembers: 3` and supervisor instructions that batch the
three initial specialists, then separately batch the two reviewers.

Execution `ef301ebd`; agent run `b7e29820-5b5b-4610-b5f9-e71a22118063`:

- Specialists started at sequences 7, 8 and 9, within 2.372 ms. The first specialist
  completed at sequence 314. Reviewers also started together, at 379 and 380.
- 27 `team_board` deliveries; 712 events with exact sequence 1 through 712.
- No model transport failure or shared-context concurrency error observed.
- Failed after 394.02 seconds at the shared model-call limit: 99 investigation
  calls, 171 tool calls and seven delegations. The separate final-report call
  remained reserved. Reviewers retained unresolved dump-analysis questions;
  subsequent attempted approval without new observations was correctly rejected.

This diagnostic run validates concurrent execution and persistent journal ordering,
but **does not establish a successful completed diagnosis**. The existing 180-day
investigation needs enough budget and a resolvable diagnostic scope. Review and
evidence requirements were not weakened to make the smoke green.

## Live diagnostic workflow: one day

The same published workflow was run again with `zeitraumTage=1` after the API
restart. Execution `e1005902-36df-4c32-8e22-ce2395baf72e`, agent run
`796c2415-11dc-49de-b091-30d51200eb5e`:

- Workflow and agent transport status **Succeeded** after 418.44 seconds;
  structured diagnostic outcome **partial**. The report retains unresolved
  event-time attribution and cause, instead of claiming a proven BSOD diagnosis.
- 93 model calls, 153 tool calls, 11 delegations, four overlapping assignment
  batches and 21 `team_board` deliveries.
- Exact event sequence 1 through 620; unique, contiguous model-call numbers;
  no model transport failure. Both reviewers completed their checks.

The live exercise verifies that the full workflow can finish with a schema-valid
report while preserving diagnostic limitations. Neither this different time window
nor the earlier failed 180-day run supports a controlled wall-time speedup claim.

Local exported journals are retained under `.runlogs/parallel-team/` and were not
added to the repository because they contain machine diagnostic data.
