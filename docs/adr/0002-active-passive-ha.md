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
- Failover cancels executions owned by the previous node; operators retry them explicitly.
- Database HA remains an operator responsibility.

Cluster mode is opt-in via `Cluster:Enabled=true`. In that mode startup validates shared JWT
settings and a cluster-portable secret protector. DPAPI is rejected because a standby node cannot
decrypt another node's DPAPI ciphertext.

## Konsequenzen

- Single-node remains the low-complexity default.
- Two-node deployments get deterministic trigger singleton behavior and crash failover.
- Workflows do not continue mid-step across process loss; they fail closed as cancelled.
- The HA feature depends on the secret-provider decision in ADR 0004.

## Referenzen

- [../ha-active-passive.md](../ha-active-passive.md)
- [../enterprise-features.md](../enterprise-features.md)
