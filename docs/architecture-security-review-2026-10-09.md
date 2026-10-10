# Architecture and security review — 2026-10-09

Baseline: `a96eb96`. Working branch: `audit/architecture-security-2026-10-09`.
Method: `improve-codebase-architecture`, `audit-ai-slop-code`, additional structural
review, independent counter-review of candidates and fixes, and executable regressions.
Severity describes the demonstrated application impact under [the threat model](threat-model.md).
Trusted Operator code execution and administrator-managed integrations are intentional;
folder authorization, retained caller identity and external-content confinement still apply.

Review reopened: the previous closure overstated the repeated-review scope. The initial broad
audit was followed by focused fix/regression reviews, not repeated full-project rounds. The eleven
fixes below remain validated, but completion now requires a documented new full-project round,
and another full-project round after any resulting corrections.

Round 2 is now a completed findings round, with corrections and focused regressions recorded in
[API/Core](audit-rounds/round-2-api-core.md), [runtime](audit-rounds/round-2-engine-data.md),
[clients](audit-rounds/round-2-clients.md), and [AI/delivery/configuration](audit-rounds/round-2-ai-delivery.md).
The renewed full-project [Round 3](audit-rounds/round-3.md) is a completed findings round.
Its corrections are followed by the fresh full-project [Round 4](audit-rounds/round-4.md)
with rotated reviewers. Round 4 is paused at the user's request; completion is not claimed.
The [pause/resume record](audit-rounds/pause-2026-10-09.md) identifies unfinished coverage,
open candidates and the final validation of the current working tree.
The German [list of all corrections and possible everyday consequences](audit-rounds/fix-list-de.md)
includes both the first eleven items below and the subsequent findings.

## Critical

No confirmed finding.

## High

### H1 — Nested Workflow Executions borrowed the child publisher's identity — corrected

- **Description:** Synchronous `startWorkflow` and `forEach` children lost the original initiator.
- **Attack Scenario:** An Operator permitted to run a published child could reach its restricted
  grandchild using the child's Admin publisher instead of the original Operator's folder grants.
- **Affected Files:** [StartWorkflowActivity](../src/NodePilot.Engine/Activities/StartWorkflowActivity.cs),
  [ForEachActivity](../src/NodePilot.Engine/Activities/ForEachActivity.cs),
  [SubWorkflowInvocation](../src/NodePilot.Engine/Activities/SubWorkflowInvocation.cs).
- **Exact Root Cause:** Synchronous engine calls omitted `startedByUserId`; subsequent authorization
  fell back to the immediate Workflow's publisher. The asynchronous path already carried the identity.
- **Fix Recommendation:** Implemented propagation of the parent initiator, resolving the stable parent
  publisher once when necessary. Regressions cover both invocation Activities and a nested
  authorization check that must deny use of the child's Admin publisher.

## Medium

### M1 — Same-folder calls bypassed the global role cap — corrected

- **Description:** A demoted Viewer could continue invoking sibling Workflows from a running parent.
- **Attack Scenario:** Demote an initiator to Viewer while its parent is running; a later child call
  in the same folder passes even though the account no longer has execution privileges.
- **Affected Files:** [SubWorkflowAuthorizationResolver](../src/NodePilot.Api/Security/SubWorkflowAuthorizationResolver.cs).
- **Exact Root Cause:** The same-folder success return preceded the Operator/Admin role check.
- **Fix Recommendation:** Implemented account and role checks before the same-folder optimization;
  tests retain allowed Operator/Admin behavior and deny demoted, inactive and stale principals.

### M2 — Queued child calls used stale authorization and enablement — corrected

- **Description:** A child selected before a concurrency wait could execute after disablement or
  permission revocation; successive `forEach` iterations retained the same stale definition.
- **Attack Scenario:** Disable a queued child or revoke access while a parent waits for capacity.
  Releasing capacity previously started the earlier snapshot.
- **Affected Files:** The three Engine invocation files listed under H1.
- **Exact Root Cause:** Child resolution and authorization happened before waits, without a general
  recheck at actual synchronous invocation.
- **Fix Recommendation:** Implemented a shared reload/enablement/authorization operation immediately
  before each child invocation, using its scoped database context. Tests change state during the wait
  and between iterations. This is admission-time validation, not cancellation of already-running work.

### M3 — Live subscriptions retained access after folder moves — corrected

- **Description:** Operations-feed folder snapshots retained the old ancestry; cancellation after
  commit or a concurrent join could also leave execution/workflow subscriptions authorized under it.
- **Attack Scenario:** A Viewer subscribes before an administrator moves a subtree to a restricted
  parent and continues receiving status/error events despite REST access being denied.
- **Affected Files:** [ExecutionHub](../src/NodePilot.Api/Hubs/ExecutionHub.cs),
  [SharedWorkflowFoldersController](../src/NodePilot.Api/Controllers/SharedWorkflowFoldersController.cs).
- **Exact Root Cause:** The move invalidated ordinary groups but not operations-feed snapshots;
  post-commit revocation depended on request cancellation and joins could race with the move.
- **Fix Recommendation:** Implemented snapshot refresh that clears restricted access before I/O,
  serialized authorization/join and move operations, and captured subscriptions before commit so
  revocation needs neither another database read nor the cancelled request token. Regressions cover
  old/new/Admin readers, empty subtrees, refresh failure, cancelled requests and concurrent joins.

### M4 — LLM response limits ran after unbounded buffering — corrected

- **Description:** Both non-streaming dialects could buffer an oversized upstream response before
  enforcing the advertised 16 MiB cap.
- **Attack Scenario:** A faulty or compromised configured model endpoint sends a very large body;
  concurrent calls consume process memory before the parser's limit can reject it.
- **Affected Files:** [LlmHttpTransport](../src/NodePilot.Ai/LlmHttpTransport.cs),
  [OpenAiCompatibleLlmClient](../src/NodePilot.Ai/OpenAiCompatibleLlmClient.cs),
  [OpenAiResponsesLlmClient](../src/NodePilot.Ai/OpenAiResponsesLlmClient.cs).
- **Exact Root Cause:** Callers selected `ResponseContentRead`, which buffered before the transport's
  limited stream; size enforcement was therefore outside the first allocation path.
- **Fix Recommendation:** The transport now owns headers-first delivery for all callers, bounded
  body reads and body-timeout/network-error classification. Tests exercise both public clients,
  success/error bodies, declared/unknown lengths, caller cancellation and interrupted connections.

### M5 — Knowledge corpus links escaped the selected root — corrected

- **Description:** Documentation search/read followed linked descendants outside the selected corpus.
- **Attack Scenario:** A configured live documentation tree includes a junction to confidential
  documents. A Viewer asks the knowledge assistant to read/search those documents under the service
  identity. A link cycle can also prevent a search from making progress.
- **Affected Files:** [KnowledgeFileSearch](../src/NodePilot.Ai/Knowledge/KnowledgeFileSearch.cs).
- **Exact Root Cause:** Read confinement compared lexical paths only; traversal followed reparse
  points and counted only eligible files toward the search budget.
- **Fix Recommendation:** Implemented descendant reparse-point rejection and bounded traversal in
  the shared corpus module. An explicitly selected root junction remains supported. Eight tests
  exercise both public readers, external files/directories, cycles and an intentional root junction.
  Concurrent malicious filesystem replacement by a trusted host actor remains outside this model.

### M6 — Startup recovery left paused Step Executions orphaned — corrected

- **Description:** A cancelled Workflow Execution retained permanently paused Step Executions.
- **Attack Scenario:** Restart during a debugger pause; the parent becomes terminal while step state
  and operations indicators remain active indefinitely. No attacker is required.
- **Affected Files:** [StartupRecovery](../src/NodePilot.Engine/Execution/StartupRecovery.cs).
- **Exact Root Cause:** Single-node recovery selected only Running steps, unlike the shared terminal
  lifecycle's Running-or-Paused rule.
- **Fix Recommendation:** Implemented both states in the existing atomic recovery batch, retaining
  its transaction behavior. Regression coverage includes paused steps and unaffected terminal steps.

### M7 — Capacity rejection left Running executions behind — corrected

- **Description:** A synchronous child rejected by the global capacity cap retained a Running row
  and unbalanced lifecycle metrics.
- **Attack Scenario:** Saturate capacity and request another child; its parent records failure,
  but the rejected execution remains active until restart. No malicious author is required.
- **Affected Files:** [WorkflowEngine](../src/NodePilot.Engine/WorkflowEngine.cs).
- **Exact Root Cause:** Capacity reservation occurred after the Running row was saved but outside
  the terminalization/cleanup `try` block.
- **Fix Recommendation:** Implemented reservation inside that lifecycle, terminalizing rejection
  through the existing failure handler and releasing a slot only if reservation succeeded. The
  original capacity exception still propagates. The persisted-state regression failed before the fix.

### M8 — SCOrch pagination could send Windows authentication outside its origin — corrected

- **Description:** Server-provided pagination URLs were followed as new authenticated requests
  without limiting their origin or transport scheme.
- **Attack Scenario:** A compromised configured SCOrch endpoint returns an absolute `nextLink`
  pointing to an attacker-controlled host, which can challenge the Switcher's Windows identity.
  This requires a manipulated SCOrch response; control by an ordinary runbook author was not shown.
- **Affected Files:** [ScorchApiClient](../src/NodePilot.Switcher/Services/ScorchRunbookReconciler.cs).
- **Exact Root Cause:** `GetAsync(nextLink)` reused a client with default Windows credentials for
  arbitrary absolute URLs. Configured collection/mutation paths had the same request seam.
- **Fix Recommendation:** Implemented scheme/host/port confinement before every request, rejecting
  userinfo and off-origin URLs while preserving relative and same-origin absolute paths. Fifteen
  new tests cover pagination and initial/mutation requests. This is not a claim that .NET's automatic
  redirect handler forwards default credentials; redirects and pagination are different paths.

### M9 — Nested children could exhaust their own admission pool — corrected

- **Description:** A synchronous ancestor retained the capacity slot required by its descendant.
- **Attack Scenario:** An acyclic `A -> B -> C` chain fails at sub-workflow capacity one; many
  simultaneous parents can reproduce the same starvation at the production capacity. A regression
  demonstrated failure despite the leaf being immediately executable.
- **Affected Files:** `startWorkflow`, `forEach` and the Workflow Scheduler.
- **Exact Root Cause:** Capacity ownership covered the entire awaited call, without representing
  suspended parents or active sibling steps.
- **Fix Recommendation:** Implemented scheduler-aware shared leases as specified in
  [ADR 0018](adr/0018-active-subworkflow-capacity.md), with unchanged Engine/per-Workflow limits
  and regressions for parallel branches, nested loops and cancellation.

### M10 — Isolated PowerShell discarded output when a pipe remained open — corrected

- **Description:** A completed script could lose its output and be reported as never having started.
- **Attack Scenario:** An unrelated concurrently launched process inherits a pipe write handle;
  the script exits, but the reader cannot reach EOF before the bounded drain expires. No malicious
  script is required. Already emitted error records could also be lost.
- **Affected Files:** [ProcessExecutionEngine](../src/NodePilot.Engine/PowerShell/ProcessExecutionEngine.cs).
- **Exact Root Cause:** `ReadToEndAsync` kept the captured text private until EOF; the timeout path
  returned an empty string for a still-pending read. The existing regression only tested the side
  that had already reached EOF. A deterministic stream test delivered the wrapper marker and
  output, held EOF open, and demonstrated loss of all 39 already-read characters.
- **Fix Recommendation:** Implemented incremental chunk capture and snapshots on bounded
  drain expiry, retaining job termination, cancellation and late-fault observation. A failed reader
  must still fail if the other pipe remains open; cancellation during draining must propagate.
  Do not retry
  script execution or extend the drain deadline to mask lost output.

## Low

In the initial round, no additional actionable low-severity finding was retained. Round 2's
concrete low corrections are documented in the linked round reports. File size, long comments and hypothetical
interfaces alone were not promoted to defects. Existing ADR-backed HTTP client separation and
trusted-author execution were preserved.

## Summary

The first eleven confirmed findings (one High, ten Medium) are corrected and independently
counter-reviewed. Focused final checks found no further confirmed Critical, High or Medium
finding in those changed seams; that did not establish the requested repeated full-project
coverage. The full Engine rerun after those corrections passed with zero failures.
Changes remain uncommitted on the working branch; no deployment or external publication was made.

### Executed validation

| Check | Result |
|---|---|
| Full `NodePilot.Ai.Tests`, Release | 715 passed, zero skipped |
| API authorization/folder-move/SignalR regression selection, Release | 78 passed, zero skipped |
| Engine invocation/recovery/capacity/scheduler regression selection, Release | 137 passed, 10 existing provider-dependent skips |
| Engine PowerShell namespace after output-capture correction, Release | 276 passed, zero skipped |
| Full Engine suite after all corrections, Release | 3,047 passed, zero failures, 10 existing provider-dependent skips |
| Full Switcher suite, Release | 102 passed, zero skipped |
| Full CLI suite | 592 passed |
| Full MCP suite | 206 passed |
| Full desktop Vitest suite | 123 passed |
| SPA auth/client/lookup/Markdown selection | 63 passed |
| `dotnet list NodePilot.slnx package --vulnerable --include-transitive --format json` | No reported vulnerabilities |
| `npm audit --json` in SPA, docs, desktop and marketing-film | No reported vulnerabilities in any workspace |

The first full Engine run passed 3,043 tests, failed one concurrent process-isolation test and
skipped 10 provider-dependent cases. Its missing-output symptom prompted M10's deterministic
regression; the original concurrent scheduling sequence did not reproduce in isolated or selected
mixed runs, so an exact causal link to that particular occurrence was not established.
Existing compiler/analyzer warnings
were emitted by the test builds. An initial simultaneous CLI/MCP Debug build hit a shared compiler
output lock; the sequential rerun passed. No test assertion was weakened or test disabled.

Commands for the changed backend seams:

```powershell
dotnet test tests/NodePilot.Ai.Tests -c Release --no-restore
dotnet test tests/NodePilot.Engine.Tests -c Release --no-restore
dotnet test tests/NodePilot.Api.Tests -c Release --no-restore --filter 'FullyQualifiedName~SubWorkflowAuthorizationResolverTests|FullyQualifiedName~SharedWorkflowFoldersControllerMoveTests|FullyQualifiedName~ExecutionHubTests|FullyQualifiedName~SignalRExecutionNotifierTests'
dotnet test tests/NodePilot.Switcher.Tests -c Release --no-restore
```

### Structural decisions

The useful deepening opportunities were the child-invocation module, LLM transport module and
knowledge-corpus module: their callers had to remember security invariants that belonged inside
the module's interface. Consolidation improves locality and allows tests through actual caller
interfaces. Existing lifecycle transitions were reused instead of adding a second cleanup path.
The API subscription seam now coordinates permission mutation with subscription publication.

### Scope and limits

Reviewed surfaces include API routing/authentication/authorization, workflow and execution reads
and mutations, SignalR, external triggers/webhooks, AI/agent inputs, backup envelope and secret
protection, Engine/Scheduler/Data lifecycle, SPA/desktop/CLI/MCP clients, Switcher, deployment and
CI configuration, and all four npm lockfiles plus the solution's NuGet graph.
The repository uses ASP.NET/EF with PostgreSQL or SQL Server; Supabase/RLS/GraphQL checks are not
applicable. The API inventory covered 42 controllers with 202 HTTP-method attributes.

This is source review and local regression evidence, not proof of absence of all vulnerabilities.
No production deployment, real AD/OIDC/SCIM login, remote WinRM estate, installer/release-lab matrix
or external penetration test was run. Full-history Gitleaks was not run locally; tracked sensitive
filenames, selected history and the configured CI secret scan were inspected. Full-repository
coverage/E2E gates remain CI responsibilities and were not represented as locally executed.
