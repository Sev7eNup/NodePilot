# ADR 0016: General AI agent activities and sequential teams

Date: 2026-09-18. Status: Accepted; sequential-team portions superseded by [ADR 0017](0017-parallel-team-delegation.md).

## Decision

Implement `aiAgent` and `aiAgentTeam` as ordinary executable Workflow Activities. The
Workflow Engine remains a DAG scheduler. Iteration and delegation take place inside
the Activity through Microsoft Agent Framework 1.21.0. An `IChatClient` adapter wraps
the existing `ILlmClient` transports; the existing chat orchestration and `llmQuery`
are unchanged. Framework code belongs in Ai, execution/remote tools in Engine and
shared contracts in Core. No framework workflow engine is introduced.

Agents are general purpose. Windows/SCCM diagnosis is an example skill, not a product
boundary. A team has one technical supervisor and up to eleven sequential specialists,
each with a stable member ID, free role, instructions, selected tools, target/credential
binding and session. Delegation returns `completed`, `needs_input` or `failed` to the
supervisor. It does not suspend for a human conversation. Members are a render
projection of configuration, never separate graph nodes, workflow edges or variables.

## Authorization and trust

Teams share a bounded investigation register through host-provided read/update
tools. Entries record question, owner, hypothesis, original evidence and
counterevidence references, next check, status and conclusion/limitation. An open
entry prevents completion; changes invalidate dependent reviews. Resolved entries
require existing run evidence IDs. Blocked entries require an explicit limitation.
Entries remain model-authored claims: the host validates structure and references,
not the semantic truth of a conclusion or the completeness of registered questions.
Updates are persisted before publication and never count as fresh observations.
Updates patch explicitly supplied fields; omitted fields retain their previous value.
Closed-check edits return an explicit rejected-change result with the current check
and reopening instructions, without changing it or invalidating reviews. Approval
is a reviewer response, not another rewrite of the investigation conclusion.
No new permissions, external tools, technical roles or database tables are added.

Final result synthesis is a separate tool-free call within the shared budget; one
call is reserved for it. The host handles investigation model-call exhaustion separately from other resource/technical
errors: started parallel members finish, unfinished responses remain obligations,
and the reserved synthesis runs without replaying tools or raising the budget.
The host passes bounded draft/findings/check context and
validates a result envelope separately from the user's output schema. Persisted
`run_conclusion` metadata and output parameters expose task outcome
(`completed`/`partial`/`blocked`) independently of technical run success. Missing or
interrupted assessments are `unassessed`. Outcomes remain model-authored judgments;
blocked checks cannot become `completed`. A required per-deliverable coverage list
also prevents completion when it contains unresolved requirements: some fulfilled
means at most `partial`, none fulfilled means `blocked`; an explicit blocked
assessment is never upgraded. Final synthesis also receives host-recorded tool
attempt/success/failure counts and bounded last-call input/result excerpts.
Coverage and its supporting basis
remain model-authored; the host cannot prove every requirement was enumerated or
that a claimed fulfillment is true. Existing run events carry this metadata, so
no new storage table or schema migration is needed. A durable initial report
draft survives failed finalization and is clearly labeled preliminary.

Before and after that draft, bounded journal checkpoints retain member findings,
stable investigation checks and recent tool results. They are not final assessments.
Current host review state is injected independently of compacted model history;
identical submissions reuse only still-current approvals. A per-member structural
progress watch warns after 12 unchanged turns and stops investigation tools after 24,
including in Power mode. Stopped investigations cannot be assessed as completed.
Source truncation remains distinct from snapshot excerpt truncation in evidence
envelopes and recall. These safeguards do not certify model-authored diagnoses.

Working-summary output truncation permits one shorter, tool-free summary request
against the same retained exchanges, within the shared budget. Partial notes never
enter the session. This does not retry agent activities or external tool actions.
Reviewers can locate decisive passages in immutable run evidence by literal search;
recall does not become a new external observation or extend permissions.

Publishing a workflow authorizes autonomous use of its selected tools within host policy. The chat's
existing click-to-apply rules still apply to chat proposals. There is no approval
dialog per agent tool call. MCP registrations and versioned skill packages are
administrator managed; the agent can select neither a new endpoint nor credentials.
The host resolves and checks tools, paths, workflow IDs, identities and destinations.
Tool results, files, logs and delegated answers are untrusted data. Prompt instructions
cannot grant privileges. Service identity requires an explicit agent selection and
administrator opt-in. A missing target never falls back to another machine.

Host-marked guidance from selected administrator-managed skills is task guidance,
subordinate to host policy and the configured task. Bounded main instructions remain
in the member's fixed context across compaction; references load progressively as
complete bounded pages. Guidance is not target evidence and cannot advance review
observation tracking. Script output remains untrusted. Revocation/version checks
apply before model calls and resource/script calls.

PowerShell, CMD and installed Git Bash are available locally and over WinRM.
The 2026-09-19 acceptance run exposed an MSI reconfiguration triggered by a supposedly
read-only `Win32_Product` query. The user consequently requires read-only agents now,
with explicit writes later, while retaining the existing shell tools.

A single Engine permission policy now accepts a limited parsed read language, checks
parameters and CIM classes, and rejects dynamic execution. Native shell commands execute
a canonical form; packaged scripts are checked with their bound arguments before upload.
CMD/Bash packages execute the checked canonical command form, preventing package-local
command shadowing. PowerShell retains the verified original script and signature policy.
File writes and unsupported operations fail closed. HTTP uses GET/HEAD without bodies or
redirects and existing network protection, with no mandatory URL whitelist. MCP uses explicit
administrator approvals pinned to server revisions and tool contracts; this requires trusted
read services/credentials, not blind reliance on annotations. Workflow calls inherit an
execution scope that checks definitions and resolved steps, including synchronous descendants.
No tools are replaced and no write-mode setting is introduced. Future write authorization
extends this boundary.
Details and limitations are in [the agent guide](../ai-agents.md#current-read-only-policy).
The check assumes trusted installed modules/providers and does not sandbox arbitrary shell
code. OS rights and external network policy still constrain the account. Target script
execution policy is not bypassed. Linux, SSH and WSL are outside this release.

## Execution and evidence

Team members may opt into a technical reviewer function with `isReviewer`, separate
from the free role label and mutually exclusive with supervisor. Existing definitions
default to no required reviewer. The host tracks unresolved needs_input/failed
responses and completed reviews of the current work revision. Reviews may explicitly
declare member/check source dependencies; changed sources invalidate dependent reviews.
Whole-team reviews (no declared dependencies) and new shared questions retain global
invalidation. Assignment snapshots prevent approval of concurrently changed work.
Dependency declarations do not narrow the original task or prove semantic independence.
Correction stops after
two consecutive corrective supervisor turns without new distinct observations or
fewer outstanding obligations within the existing budgets; new progress permits
further rounds. Closing an open investigation check also counts as progress.
Unresolved review or investigation proceeds to the reserved tool-free final report,
which must retain the gaps and cannot receive a completed task assessment. Successful
report delivery completes the step with a partial or blocked outcome. Material evidence
gaps must be addressed before final review; after complete checks and approvals the host
proceeds directly to final synthesis without another exploration round. Follow-up must
identify which requested conclusion or remedy the next observation could change.
This enforces protocol completion, not semantic correctness.
New original observations from every member, including other reviewers, can support
the objection owner's subsequent reassessment. The host deduplicates equal observations
by target/tool/input/result across roles; recalling snapshots and summaries does not count.
The owner still closes their own question, and judges the evidence's relevance.
Review dispatch is limited to three attempts per reviewer and distinct observation
set. Register edits and changing shell-envelope observation times do not reset it.
Further dispatch requests a discriminating read before another model call; it never
grants approval. This bounds repeated internal review loops as well as corrective
supervisor turns.
Every delegated member receives the latest other-member reports separately from the
assignment. These reports retain their untrusted status and grant no capabilities.

Single-agent defaults are 20 model calls, 40 tool calls and 20 minutes. Team defaults
are 100 model calls, 500 tool calls, 20 delegations and 30 minutes, shared by all members.
Each model request is separately bounded by 180 seconds and 250,000 output tokens by
default, configurable through Agent settings. Lower profile limits and the run deadline
still apply. A transient model request (timeout, HTTP 408/429/500/502/503/504,
identified socket reset/abort or prematurely ended HTTP response) may be
retried once after one cancellable second within the shared budgets. The adapter
reuses the request and existing tool results without replaying actions. Retry admission
atomically preserves reserved calls and an answer after working summaries/tool calls.
TLS/authentication, DNS and refused connections remain terminal.
Persistent or non-retryable failure terminates the run through nested delegation;
available preliminary findings and journal evidence remain without claiming completion.
Context compaction operates at the existing `IChatClient` adapter before every inner
model call, not just at framework session entry. Complete exchanges are summarized
into untrusted working notes; immutable redacted observations remain in the run journal.
Run-memory tools retrieve and analyze bounded sections without repeating external actions.
Summary and section-analysis requests consume the same model/time budget and do not
count as fresh evidence for review. See `docs/ai-agents.md` for limits and retention.
Admin settings set ceilings. Two active agent runs per server process is the default;
members share the team slot. Waiting child workflows release the step and agent slots;
reacquisition and children inherit the remaining run cancellation/deadline.

Agent retries are rejected during definition validation and disabled in StepRunner.
Tools with possible side effects have no automatic replay. Cancellation stops future
calls and attempts to stop in-flight work; it does not undo completed actions. Restart
ends affected executions/runs, never resumes their sessions.

AgentRun and sequence-numbered AgentRunEvent records persist before notifications.
REST catch-up is authoritative; SignalR only wakes readers. Events include member IDs.
Read authorization and retention follow the owning Workflow Execution. `agentRunId`
and usage counters live in OutputParameters, without changing ActivityResult's shape.
Final JSON is validated locally against the configured schema; one tool-free repair
round counts against the same model budget. External schema references are rejected.

Raw logs share a 250,000,000-byte quota per run and transfer in 256 KiB blocks. Bounded
target search and streaming collected-file search return excerpts with source/line
references. Raw staging is temporary; results and used evidence remain in the journal.
Skills are immutable versioned ZIP packages with root SKILL.md, resources and optional
scripts. The runner stages selected package files on the bound target, verifies SHA-256
and cleans up. Script execution additionally requires the matching shell tool.

## Consequences

Two separate AI orchestrations are deliberate until a later chat migration. MCP is a
client integration (stdio and Streamable HTTP); the existing public NodePilot MCP
server remains stdio. Parallel specialists, nested teams, durable agent resumption,
an indexed log store and a permanent raw-artifact store are outside this decision.
The three development sections form one feature scope, not three reduced releases.
