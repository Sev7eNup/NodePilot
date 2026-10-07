# ADR 0017: Parallel team delegation and live peer pointers

Date: 2026-10-06. Status: Accepted. Scope: `aiAgentTeam` inside one Workflow Activity.
Supersedes the sequential-team portions of [ADR 0016](0016-general-ai-agent-activities.md).

## Context

Independent specialists currently wait for each other. Concurrent execution must preserve
shared limits, durable event cursors, isolated member sessions and strict review gating.

## Decision

The supervisor calls `delegate` with `assignments: [{memberId, task, reason}]`.
The effective limit is bounded by workflow `maxParallelMembers`, administrator
`Agents:TeamMaxParallelMembers` (default 3), and non-supervisor member count. IDs must
be distinct; reviewers and non-reviewers cannot share a batch. Supervisor tool
invocation remains sequential, and a member never executes two assignments at once.

Validate before charging. Atomically charge one tool call per assignment and one
delegation per started member, including no extra wrapper charge. Review attempts
are recorded only after budget reservation; progress-required slots do not start a
member. Requests are snapshotted before starting any assignment. Reviews record the
revision they received, so concurrent material changes require renewed review.

Run accepted assignments with `Task.WhenAll`. Model/transport failures cancel the
shared run, and every sibling is awaited before finalization. Ordinary assignment
failures return failed slots; they still block completion until addressed. Return
`{batchId, results:[{delegationId,memberId,status,content,objectionKind,contentTruncated}]}`
in input order. Truncate individual content, never the serialized JSON envelope.
Event content carries delegation and batch IDs; no database migration is needed.

A run-local TeamBoard keeps at most 256 bounded evidence/register pointers. Each
assignment begins at the current cursor; before subsequent tool-enabled model calls,
deliver only new peer entries, newest first, with an omitted count. Attach the delta
to the last tool message of the transport view after reserving its context space.
Session history and compaction fingerprints remain unchanged. Journal delivery IDs
as `team_board`. Pointers are untrusted working context, never observations and never
a substitute for original evidence reads or review requirements. There is no polling
tool or persistent board.

## Concurrency invariants

- All shared step-context database access uses scoped `AgentRunDatabase`. The journal
  assigns sequences and commits within that gate; detach failed entries and roll back
  the in-memory sequence on failure. Notifications occur after releasing the gate.
- Serialize register updates across read, merge, persistence and revision publication.
  Register gate precedes database gate. Other state uses leaf locks, never held across
  `await`. Evidence/analysis IDs and evidence/artifact quotas are reserved before I/O;
  failed persistence/transfers return their quota, without recycling evidence IDs.
- Budget counters and usage snapshots are atomic. Completion state is synchronized.
- A separate per-lease gate serializes `workflow_run` release sections. Child workflows
  resolve `StartWorkflowActivity` in their own DI scope. Waiting releases scheduler and
  agent slots. Other members may continue without those slots during that wait; this
  deliberate trade-off avoids deadlock while preventing concurrent lease toggles.

## Consequences and limits

Independent work can reduce wall time, but actual model timing is variable. Prove
overlap with deterministic barriers and journal ordering, and report live elapsed
times without treating a single faster run as a universal guarantee. Concurrent
model calls increase rate-limit risk. A transient model request (timeout, HTTP 408/429
or 500/502/503/504, identified socket reset/abort or prematurely ended HTTP response)
is retried at most once within shared call/time budgets. This retry
does not replay tools or delegations; persistent failure still fails the shared run.
The default of three stays below the default five WinRM sessions per machine.

Nested teams, direct member calls, queued oversized batches, parallel child workflows,
concurrent supervisor delegation calls and restart resumption remain out of scope.
