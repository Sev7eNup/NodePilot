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
