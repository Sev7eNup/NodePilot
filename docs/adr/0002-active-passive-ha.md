# ADR 0002 - Active/Passive HA

**Status:** Implemented - 2026-05-09
**Scope:** Optional two-node application-layer failover for production deployments.

## Kontext

NodePilot started as a single-node Windows service. That is the simplest and still default
deployment model, but some production sites need planned maintenance and crash failover without
manually starting a standby instance. The application can coordinate ownership through the shared
database, but it cannot replace database HA or make in-flight PowerShell work process-portable.

## Entscheidung

NodePilot supports **active/passive HA**, not active/active horizontal scaling.

- Exactly one node is leader at a time, elected through a database-backed lease.
- Only the leader starts trigger sources and accepts leader-only work.
- Load balancers route normal traffic to the node whose `GET /healthz/leader` probe is healthy.
- Failover cancels previously running/paused executions and orphaned Pending rows; operators retry
  them explicitly. Pending rows with a durable dispatch intent are adopted by the new leader.
- Database HA remains an operator responsibility.

Cluster mode is opt-in via `Cluster:Enabled=true`. In that mode startup validates shared JWT
settings and a cluster-portable secret protector. DPAPI is rejected because a standby node cannot
decrypt another node's DPAPI ciphertext.

### Bounded recovery transactions (2026-09-15)

Recovery selects at most 100 candidate execution IDs before acquiring the lease-row lock.
Each batch then revalidates owner, epoch and lease expiry in its own transaction. Execution/step
updates, reservation cleanup and recovery audit entries commit together. Log forwarding happens
after commit, outside the lease lock.

The default per-batch budget is two seconds, capped below the lease-query timeout. Timed-out
batches roll back and shrink; a single execution that cannot finish within the budget defers the
remaining sweep. The recovery host retries after five seconds while it still owns the same epoch
and cancels promptly on leadership loss. Committed progress is retained. An uncertain commit is
verified before replay, so recovery cannot duplicate its audit record or claim progress prematurely.

## Konsequenzen

- Single-node remains the low-complexity default.
- Two-node deployments get deterministic trigger singleton behavior and crash failover.
- Workflows do not continue mid-step across process loss; they fail closed as cancelled.
- The HA feature depends on the secret-provider decision in ADR 0004.

## Referenzen

- [../ha-active-passive.md](../ha-active-passive.md)
- [../enterprise-features.md](../enterprise-features.md)
