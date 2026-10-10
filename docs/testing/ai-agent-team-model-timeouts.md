# Team model-response stalls

Date: 2026-09-20. Branch: `feature/ai-agent-activities`.
Follows [content diagnosis and remote logs](ai-agent-content-and-remote-logs.md).

## Findings and limits of the diagnosis

The three retained cancelled runs had completed all reviewer log searches. Their
next `model_started` event coincides with an outgoing Responses HTTP request in the
development server log, with no matching response headers before cancellation.
There is no evidence of a delegation-slot or file-transfer deadlock at that point.
Even limiting the CAS search to one 360-character result did not prevent the earlier
symptom. Tool-call exhaustion was not involved (13 of 500 calls).

The active profile allowed 1,000,000 output tokens and a 3,600-second request timeout.
Agents inherited both. The team's 1,800-second total deadline was therefore the
first effective deadline for an unresponsive model. No member-specific model failure
event explained the wait. The transcript-size limit only bounded input, and the
result-size check ran after a complete answer; neither bounded generation in flight.

A replay harness fed stored tool observations through the real Agent Framework
runtime and Responses client. Responses arrived in seconds in both non-streaming
and streaming comparisons; no endless whitespace generation was observed. A captured
request from a complete new team run also completed when replayed. The new full
run, before the production correction, succeeded in 65.8 seconds
(`b5e92501-4934-491b-a1ce-4f361fd9b566`). A separate retry failed immediately with
an invalid upstream JSON response (`c2eaf0f4-62a9-42c4-8c32-f64038416c6d`); this was
not a reproduction of the long wait.

Six sequential replays through NodePilot's configured HTTP factory also completed
in 9.6–11.7 seconds, including connection reuse. Another captured request completed
in 32.9 seconds non-streaming and 10.6 seconds streaming. These comparisons do not
establish a deterministic connection-pooling, JSON-mode or payload-size defect.

The original upstream/network reason for the missing responses cannot be proved
from the retained evidence. Neither a specific provider defect nor runaway generation
is claimed. The reproducible NodePilot defect is its missing per-model bound and the
absence of a fatal model-error boundary across nested delegation.

## Correction

- `Agents:ModelCallTimeoutSeconds` defaults to 180 seconds and covers the complete
  model request, including response-body reading. The profile and total run may impose
  shorter deadlines. Agent settings expose this value.
- `Agents:ModelMaxOutputTokens` defaults to 16,384. The smaller of this and the profile
  limit is sent to the provider. Input remains 250,000 characters; the tool ceiling
  remains 500. Existing chat and `llmQuery` configuration is unchanged.
- A model transport error or truncated response journals `model_failed` with the
  member ID and fails the entire run. Shared cancellation crosses the framework's
  delegation tool boundary, so the supervisor cannot silently retry a failed model
  request. Caller cancellation remains cancellation. Incomplete tool calls are rejected.
- Runtime resources are released through the existing runner cleanup path. No action
  rollback or automatic replay is introduced.

## Regression coverage

The reproduction test uses the real team runtime, adapter, Responses serializer and
HTTP transport. A supervisor delegates to a reviewer, the reviewer reads evidence,
then the HTTP handler either withholds headers or stalls the response body. Both
tests failed before the correction by reaching the outer five-second cancellation.
With a one-second agent model deadline they fail promptly with the intended timeout,
cancel the HTTP work, identify the reviewer, and issue no fourth model request or
second tool read. A lower profile output limit is also preserved.

Runner integration checks persist the terminal journal and reacquire the single
execution slot after model deadlines, total deadlines and caller cancellation for
both single agents and teams. A truncated model response cannot execute its tool call.

Validation: 553 AI tests, 279 targeted Engine/catalog tests and 3 settings consistency
tests passed. TypeScript compilation passed. The API build completed with no errors
and 54 existing warnings outside this correction. This is not a full CI/E2E run.
After preserving usage counters for truncated responses, the 29 affected AI tests
were rerun successfully and the final incremental API build passed.

## Live validation

The corrected development API ran the three-member supervisor/collector/reviewer
workflow on CLIENT1 using the unchanged read-only native tools. Five files totaling
250,000,000 bytes included a real CAS snapshot, a two-million-character line, UTF-8
and both UTF-16 byte orders. The independent controller compared source and local
SHA-256 hashes, checked the shared quota and verified workspace deletion.

The bounded-excerpt run `4c31f36c-0f3f-46d6-ae6d-6209a12d8f6a` succeeded in 62.6 seconds
with 8 model calls, 13 tool calls and 2 delegations. All hashes, six markers, Unicode,
quota rejection and cleanup checks passed.

| Execution | Result | Duration | Evidence |
|---|---|---:|---|
| `905ab54a-fe04-4b79-8c43-372b18445ee0` | Failed, model timeout | 225.5 s total | The original wait recurred after the larger CAS excerpt. `model_failed` and `run_failed` followed the reviewer request after 180 seconds, without controller cancellation or additional calls. All five hashes matched and workspace deletion passed. |
| `5f6bd02f-7f3d-4045-b3d6-6266a32fdf3b` | Succeeded | 65.0 s | Full 14,125-character CAS excerpt; all hashes, markers, Unicode, quota and cleanup checks passed. |
| `37fb8f39-c0c4-4bd3-8b71-a620b6afad81` | Workflow succeeded; report assertion failed | 116.2 s | Follow-up delegation completed without a stall. The final answer described only the review correction and omitted the six previously found markers. This is retained as an open result-completeness issue, not counted as a full acceptance pass. |

The repeat also exposed the existing quota-error handoff limitation: the collection
size exception reaches the model as `Error: Function failed`, while the journal
contains the concrete 250 MB reason. A reviewer consequently asked for additional
evidence. This does not explain a blocked HTTP request and was not changed as part
of the model-timeout correction. Full feature acceptance still needs the final-answer
completeness and error-handoff issues addressed.

Evidence and the isolated replay harness are retained under the ignored
`.runlogs/team-hang/` and `.runlogs/agent-update-validation/` directories. The temporary
request-capture instrumentation was removed from production source. API keys were
decrypted only in the replay process and were not written to captures or logs.

At 16:10 UTC the controller removed the temporary CLIENT1 log fixtures, host-access
adapter and firewall rule, and restored host TrustedHosts to `localhost`. Execution
policy remained `Undefined`; CLIENT1, CM1 and the gateway remained running. The four
post-correction live-run windows contained no MSI or Windows Update installation
events on CLIENT1 or CM1. Test workflows were disabled and the final development
API and designer both answered HTTP 200.
