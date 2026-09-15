# ADR 0014 - Durable Execution Dispatch

**Status:** Implemented - 2026-08-26
**Scope:** Admission-to-engine handoff, restart/failover recovery, and execution state transitions.

## Kontext

An accepted execution previously consisted of a durable `Pending` row plus an in-memory queue
callback. A process crash between those two worlds lost the complete dispatch intent. Startup had
to cancel the row, even though no activity had started. The bounded queue also mixed backpressure,
retry durability, and worker ownership, which created deadlock and capacity-ghost failure modes.

## Entscheidung

Admission persists the `Pending Execution` and one `ExecutionDispatchOutboxItem` in the same
database transaction. The outbox contains the complete dispatch policy and an encrypted parameter
payload. A leader-gated worker claims entries with a database lease, processes interactive entries
before normal entries, and removes the entry only after engine ownership or a definitive
pre-ownership terminal result.

The lifecycle boundary is:

```text
Pending + protected Dispatch Intent (one transaction)
  -> leased outbox claim
  -> Pending-to-Running database CAS
  -> engine-owned execution
  -> Running/Paused-to-terminal database CAS
```

Process restart and HA failover release stale outbox leases and preserve `Pending` rows that still
have a dispatch intent. `Running` and `Paused` rows are never replayed because their external side
effects are ambiguous. Confirmed database failures before engine invocation release the lease for
retry; failures after invocation never start a second execution.

Execution claims, terminal writes, dispatch failures, and direct cancellation use the shared
`ExecutionStateLifecycle` database transitions. In-memory cancellation remains a latency
optimization, not the source of truth.

### Atomic claims and idle polling (2026-09-15)

The worker pool shares one process-local claim gate. Its holder waits for a signal or a one-second
fallback when no item is available; no database connection or transaction survives that wait.
A successful reservation releases the gate before workflow processing. Workers still remain occupied
for the complete workflow lifetime, so `WorkerCount` remains the dispatch concurrency limit and
there is no prefetched queue of leased requests.

This reduces database commands, but also serializes reservation round trips. A large burst can
therefore wait longer to start, especially with higher database latency; raising `WorkerCount`
does not remove that reservation limit. Workflow execution remains parallel. The isolated
100-request benchmark measured 1,029 claim commands before this change and 107 after it, while
release-to-claim p95 increased from 408.9 ms to 624.1 ms in that run. See
[the performance measurements](../performance-improvements.md) for the workload and limitations.

One statement reserves one item: PostgreSQL uses `FOR UPDATE SKIP LOCKED` with `UPDATE RETURNING`,
SQL Server uses an ordered updateable CTE with `UPDLOCK`, `READPAST`, `READCOMMITTEDLOCK` and
`UPDATE OUTPUT`. The SQL Server hints support RCSI; competing scan locks can temporarily produce
an empty result, which the next poll revisits. SQLite tests use `UPDATE RETURNING`.

Eligibility includes availability time, lease expiry and blocked workflow IDs before selecting the
first row. Ordering is priority descending, creation time ascending, then execution ID ascending.
Existing strict interactive priority is retained; this decision does not introduce priority aging.

Claims use EF's command/connection interceptors and configured timeout without automatic execution-
strategy replay. If commit succeeds but the response is lost, the worker does not start an activity;
the reserved item becomes eligible after its existing 60-second lease expires. Claim counts and
duration use only the low-cardinality result labels `success`, `empty` and `error`. No new outbox
index is introduced without measuring the remaining claim cost after the polling change.

## Konsequenzen

- A returned `202 Accepted` survives process restart while the execution is still Pending.
- There is no trigger-event catch-up: durability begins only after a trigger fire has been admitted.
- In-flight work remains at-most-once and requires operator reconciliation after a crash/failover.
- Dispatch parameters use the configured `ISecretProtector`; HA therefore requires the shared
  AES-GCM provider already mandated by ADR 0004.
- The database migration is required before a binary with this worker starts.

## Referenzen

- [../../src/NodePilot.Api/ExecutionDispatch/ExecutionDispatchService.cs](../../src/NodePilot.Api/ExecutionDispatch/ExecutionDispatchService.cs)
- [../../src/NodePilot.Api/ExecutionDispatch/ExecutionDispatchWorker.cs](../../src/NodePilot.Api/ExecutionDispatch/ExecutionDispatchWorker.cs)
- [../../src/NodePilot.Data/ExecutionStateLifecycle.cs](../../src/NodePilot.Data/ExecutionStateLifecycle.cs)
- [0010-single-process-hosting.md](0010-single-process-hosting.md)
- [0011-database-availability-breaker.md](0011-database-availability-breaker.md)
