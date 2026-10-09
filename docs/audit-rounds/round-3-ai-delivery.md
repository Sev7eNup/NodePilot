# Round 3 — AI, delivery and support

Status: complete, findings corrected and scoped tests green. Fresh source review of the round-3 inventory; this is a findings round, not a zero-findings round.

## Coverage

| Module family | Fresh checks | Result |
|---|---|---|
| AI transport, factory, two wire dialects, proxy and configuration | Response and SSE limits, cancellation, fallback ownership, tool-call framing, effective options and egress policy | Confirmed Medium: Responses incomplete output containing function calls loses its truncation marker |
| Agent runtime, delegation, review state, evidence, investigation and context | Complete original-task retention; budget and tool enforcement; shared state synchronization; review invalidation; partial evidence and final assessment | Runtime guard is correct but depends on the lost transport marker; wire-to-invocation regression prepared |
| Workflow/script generation, merge, chat registries and knowledge | Proposal-only mutations, secret restoration, per-source privilege gates, scoped execution reads, path/extension/reparse restrictions, bounded prompts/results | No separate confirmed C/H/M; duplicate enabled custom keys can break chat catalog construction, referred to Data reviewer |
| Artifact trust, staging and server/desktop install/update/rollback/uninstall | Re-read signature/hash/ACL chain and failure paths, service/restore ordering, explicit binary-only rollback contract | No new confirmed C/H/M so far |
| CI, build and developer tools | Four workflow trigger/gating/secret/cache paths; SDK/package policy; nightly task ownership; local reset ownership | No additional confirmed C/H/M; current full transitive NuGet audit returned zero vulnerable package records |
| Operational examples, fixtures and test generators | Actual HTTP contracts, edit lifecycle, execution states, result codes; test-suite and continuous-bundle generators; provider fixture create/drop ownership; AD/LDAP lab setup and probes | Confirmed Medium: administration and demo clients drifted from current contracts (M59) |
| Monitoring, marketing and repository support | Grafana bind/auth configuration; metrics/dashboard contracts; product-tour mocked requests and output paths; shared TestCommons lifecycle and fake transports | No additional confirmed C/H/M; external/lab capabilities were not exercised |

## Confirmed candidate: incomplete Responses output reaches agent tools

- Severity: **Medium**, functional execution integrity. This is not a claim that a model can bypass host permissions.
- `OpenAiResponsesLlmClient.MapFinishReason` returns `tool_calls` before considering `status=incomplete` / `reason=max_output_tokens`.
- `LlmChatClientAdapter` rejects `FinishReason == "length"` before converting tool requests. The transport's precedence hides that signal whenever an otherwise parseable function call is present.
- A model can exhaust its output budget after emitting an earlier valid tool request. The configured agent would execute that request despite the explicit complete-response requirement. Depending on its allowed tools, this can start work from an incomplete action batch.
- Independent countercheck confirmed the same mapping serves streaming and non-streaming clients. Tests now exercise both wire paths and the actual function-invoking adapter, rather than mocking a pre-normalized `LlmResponse`.
- Corrected the shared finish-reason precedence and its misleading documentation. Three new
  regressions failed before the change (both wire paths plus actual tool invocation). The complete
  AI Release suite now passes **722/722**, with zero skips. Existing successful tool-call cases
  remain covered. The added test's nullable fixture warning was corrected; unrelated existing
  analyzer warnings remain.

## Confirmed Medium M59: operational scripts drifted from API contracts

`scripts/disable-all-workflows.py` and both `stress-test/launch-40x` launchers omit the explicit
Bearer-token response header. The stress launchers also omit the edit-lock/publish lifecycle,
and regard `Completed` rather than the actual `Succeeded` status as terminal. The 50-launcher
already opts into Bearer responses and uses the correct success state, so those specific defects
do not apply to it. Hardcoded development passwords are not asserted to be live credentials.
Independent countercheck confirmed these failures and the same missing opt-in in the demo seed.
Both alert seeds relied on a removed localhost authentication bypass and returned success even
after all writes failed. The demo seed now explicitly publishes created workflows and acquires
the required edit lock before replacement. It retains the documented delete/recreate semantics.

The Python administration clients share a standard-library HTTP module that owns bearer login,
uncapped workflow-name lookup, ambiguity rejection and lock/publish ordering. The launchers
poll their own execution IDs and return a nonzero exit code for failed or unfinished work.
The disable-all and continuous-bundle installers use the complete names endpoint rather than
the 500-row workflow details list. Alert seeds require explicit credentials and report failures.
This is contract consolidation with three real callers, not a new hypothetical interface.

Real Windows PowerShell 5.1 testing also caught provider metadata attached to `Get-Content`
strings: deep JSON serialization stalled before publication. Clean file text and explicit UTF-8
request bytes preserve the intended string contract. All **13 local HTTP fixture tests pass**,
including actual Windows PowerShell 5.1 processes; the suite is registered in backend CI. They exercise
login, create/lock/publish, own/foreign locks, ambiguity, more than 500 workflows, success/failure
terminal states and error exit codes. No actual NodePilot backend or workload is started.
An independent review found no remaining C/H/M in this change. Its two lower-severity
observations were also corrected: each poll respects the remaining time budget, and Python
launch transport errors retain the aggregate failure summary instead of aborting with a traceback.

The updated UI acceptance catalog was compared to the round-3 page/dialog changes. DES-02,
DES-04 and INF-03 now cover preserving graph edits on layout restoration, immediately running
the current script buffer, and retaining custom-activity resource limits on rename. All six
catalog tests pass. This remains catalog verification, not a completed Computer-Use acceptance.

## Limits

Generated fixtures and documentation are reviewed through their owning generators, contracts and relevant entry points; this is not a claim of individual line-by-line review of every translation or generated file. No actual installer, deployment, service mutation, database restore or release-lab scenario was run.
