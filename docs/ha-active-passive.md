# Active/passive HA for NodePilot

As of 2026-05-09. The setup for two NodePilot nodes behind a load balancer with automatic failover
(RTO ~40–60 s).

## When do you need this?

- **A customer requires "a standby takes over on a server crash" as a compliance point.**
- Planned maintenance (Windows Update, a .NET patch) should be possible without a service interruption.
- Single node at 99.9 % is not enough for you (8 h of downtime per year).

If **none of that applies**, leave it alone. Single node is simpler to operate and your SLA is
probably good enough.

## What you get — and what you do not

| Feature | A/P | Not included |
|---|---|---|
| A trigger singleton guarantee across two nodes | ✓ | — |
| Automatic failover on a node crash, RTO 40–60 s | ✓ | — |
| Automatic failover on database loss at the leader | ✓ | — |
| Crash-recoverable webhooks (a pending row + a reaper) | ✓ | — |
| Sessions survive a failover (the same `Jwt:Key`+`Issuer`+`Audience`) | ✓ | — |
| Workflows continue running through a failover | ✗ | They are marked `Cancelled`; the operator clicks retry |
| Horizontal load scaling (two nodes working in parallel) | ✗ | That would be active/active — its own stage, a separate plan |
| Database HA itself | ✗ | AlwaysOn AG / Patroni / cloud-managed RDS — the operator's responsibility |
| File-watcher events during the failover window | ✗ | They are process-local and are lost — if you need an atomic guarantee, use a webhook trigger |

## Architecture

```
                     [HAProxy / NLB]   probe: GET /healthz/leader → 200
                            │
            ┌───────────────┼───────────────┐
            ▼                               ▼
     ┌──────────────┐                ┌──────────────┐
     │ nodepilot-a  │                │ nodepilot-b  │
     │ Leader       │                │ Follower     │
     │ Lease: HELD  │                │ Lease: idle  │
     │ Triggers: ON │                │ Triggers: -  │
     │ /leader→200  │                │ /leader→503  │
     └──────┬───────┘                └──────┬───────┘
            │       DB lease (atomic UPDATE WHERE)
            └───────────────┬───────────────┘
                            ▼
              Shared SQL Server / PostgreSQL
              (the HA layer is separate: AlwaysOn AG / Patroni / RDS)
              Table ClusterLeaders
```

**The failover sequence** (after leader A crashes):
1. A's `ClusterLeaderService` can no longer renew its lease (the database is unreachable, or the process is dead).
2. B's `ClusterLeaderService` runs every 10 s and sees `ExpiresAt < db_now` → an atomic `UPDATE … WHERE OwnerNodeId='' OR ExpiresAt < now`.
3. B increments `LeaseEpoch` (+1) and is the new leader. The `OnLeadershipAcquired` event fires.
4. B's `TriggerOrchestrator` starts all trigger sources (Quartz, file watcher, etc.).
5. B's `ClusterFailoverRecoveryHost` marks every `WorkflowExecutions` row with `OwnerNodeId != "nodepilot-b"` and status `Running/Pending/Paused` as `Cancelled`, with an audit entry.
6. B's `/healthz/leader` answers **200** with the new `leaseEpoch`.
7. The load balancer notices on the next 5-second probe and routes traffic to B.

Engine terminal states are written by database compare-and-set out of `Running`/`Paused` only. In HA
mode the same `UPDATE` additionally checks `OwnerNodeId`, `LeaseEpoch` and that the `ClusterLeaders`
lease has not expired. An old leader resuming after a GC pause can therefore neither overwrite a
`Cancelled` already set by SSO offboarding nor commit `Succeeded`/`Failed` after an epoch change.

**Expected RTO: 40–60 s** (a 30 s TTL + a 10 s renew interval + a 5 s load-balancer probe).

## Prerequisites

- 2× Windows Server 2022 — with an identical `dotnet --version`. (NodePilot is `net10.0-windows`/Windows-only — WinRM + gMSA Kerberos; a Linux node is not possible.)
- An external SQL Server 2022 or Postgres 16+ — with its own HA layer
  (AlwaysOn AG / Patroni / Aurora). That **has to** exist before NodePilot; A/P only solves the
  application-layer problem, not the database-layer problem.
- A load balancer with an HTTP health probe (HAProxy, NLB, F5, AWS ALB, …).
- One shared FQDN (a VIP) pointing at the load balancer.
- An identical gMSA service account on both nodes (for WinRM Kerberos).
- An AES-GCM secret master key generated (and shared across both nodes) — DPAPI is not permitted in a cluster (see the section below).

## Configuration

```jsonc
// appsettings.Production.json — on node A
{
  "Cluster": {
    "Enabled": true,
    "NodeId": "nodepilot-a",          // node B: "nodepilot-b"
    "LeaseTtlSeconds": 30,
    "LeaseRenewSeconds": 10,
    "LeaseDbTimeoutSeconds": 3
  },
  "Jwt": {
    "Key": "<base64-32-bytes-shared>", // MANDATORY — an auto-generated jwt-secret.key would diverge
    "Issuer": "NodePilot-Prod",        // MANDATORY — both nodes have to set the same string
    "Audience": "NodePilot-Prod"       // MANDATORY — likewise
  },
  "Database": { "Provider": "sqlserver" },
  "ConnectionStrings": {
    "DefaultConnection": "Server=sql-cluster.example.com;Database=NodePilot;Trusted_Connection=True;Encrypt=True"
  },
  "Secrets": {
    "Provider": "AesGcm",                 // MANDATORY in a cluster — DPAPI is rejected at boot
    "MasterKey": "<base64-32-bytes-shared>" // identical on both nodes (through the Secrets__MasterKey env var)
  }
}
```

**Boot validation:** with `Cluster:Enabled=true`, the service fails to start if `Jwt:Key`,
`Jwt:Issuer` or `Jwt:Audience` are missing or empty — **and** if `Secrets:Provider` is `Dpapi` (or
absent): DPAPI ciphertexts are host-bound, a standby could not decrypt them after a failover, so
`SecretProtectorBootstrapFactory` enforces `Secrets:Provider=AesGcm` + `Secrets:MasterKey` here. A
clear error message names the missing or incompatible key.

## Configuration options in detail

| Key | Default | Effect |
|---|---|---|
| `Cluster:Enabled` | `false` | The master switch. False = single node (a no-op state provider). |
| `Cluster:NodeId` | `Environment.MachineName` | Identification for the lease, audit and recovery. Mandatory with container hashes. |
| `Cluster:LeaseTtlSeconds` | `30` | How long a lease stays valid. Lower = faster failover, but more sensitive to database hiccups. |
| `Cluster:LeaseRenewSeconds` | `10` | The renew interval. It should be ≤ TTL/3. |
| `Cluster:LeaseDbTimeoutSeconds` | `3` | The command timeout for lease queries. Kept low so that a hanging database renew does not block the background service. |
| `Jwt:Key` | (auto-generated) | **Mandatory in a cluster.** Base64-encoded, ≥32 bytes. |
| `Jwt:Issuer` | `"NodePilot"` | **Mandatory in a cluster.** Both nodes have to set the same string. |
| `Jwt:Audience` | `"NodePilot"` | **Mandatory in a cluster.** Likewise. |

## Secret encryption in a cluster

DPAPI `LocalMachine` is **machine-bound**. Node B cannot decrypt a credential encrypted by node A.
That affects both `Credentials` and **secret global variables** (both run through the same active
`ISecretProtector`).

That is why **the code enforces AES-GCM in a cluster**: `SecretProtectorBootstrapFactory` rejects
`Cluster:Enabled=true` + `Secrets:Provider=Dpapi` (or a missing `Secrets:Provider`, which defaults to
`Dpapi`) at boot. There is only one supported route in a cluster:

- **AES-GCM with a shared master key** (`Secrets:Provider=AesGcm`, `Secrets:MasterKey` = 32
  base64-encoded bytes, **identical on both nodes**, delivered through the `Secrets__MasterKey`
  environment variable). Both stores (credentials + secret globals) are then readable and writable on
  every node; no manual re-entry is needed. Key generation and the operator runbook:
  `docs/secrets-providers.md`.

Existing single-node instances with DPAPI-encrypted secrets have to be re-encrypted to AES-GCM once
before the cluster switch (`POST /api/secrets/reencrypt`, see `docs/secrets-providers.md`).

## Installation

Identical to single node, except:
1. Provide SQL Server / Postgres and enable the HA layer (AlwaysOn AG / Patroni).
2. Install node A via `Install-NodePilot.ps1`. Add the cluster block to `appsettings.Production.json`.
3. Generate `Jwt:Key` once with a CSPRNG (`$r=[Security.Cryptography.RandomNumberGenerator]::Create();$b=New-Object byte[] 32;try{$r.GetBytes($b);[Convert]::ToBase64String($b)}finally{$r.Dispose();[Array]::Clear($b,0,$b.Length)}`).
4. Install node B identically — the same `Jwt:Key`, a **different** `Cluster:NodeId`.
5. Configure the load balancer (HAProxy example below).
6. Smoke test: `curl http://nodepilot-a/healthz/leader` → 200, `curl http://nodepilot-b/healthz/leader` → 503 (or the other way round — whichever starts first becomes the leader).
7. Run `deploy/Test-Failover.ps1`.

## HAProxy example

```haproxy
defaults
    mode http
    timeout connect 5s
    timeout client 60s
    timeout server 60s
    timeout http-keep-alive 60s
    option http-keep-alive

frontend nodepilot_frontend
    bind *:443 ssl crt /etc/ssl/nodepilot.pem alpn http/1.1
    http-request del-header Forwarded
    http-request del-header X-Forwarded-For
    http-request del-header X-Forwarded-Proto
    option forwardfor header X-Forwarded-For
    http-request set-header X-Forwarded-Proto https
    default_backend nodepilot_active

backend nodepilot_active
    # Negotiate needs a persistent backend connection that is never shared
    # with a different frontend session.
    http-reuse never
    balance source
    hash-type consistent
    option httpchk
    http-check send meth GET uri /healthz/leader hdr Host nodepilot.contoso.local
    http-check expect status 200
    default-server inter 5s fall 2 rise 1 ssl verify required ca-file /etc/haproxy/ca/nodepilot-backend-ca.pem alpn http/1.1
    server node-a 10.0.1.10:443 check sni str(nodepilot.contoso.local) check-sni nodepilot.contoso.local verifyhost nodepilot.contoso.local
    server node-b 10.0.1.11:443 check backup sni str(nodepilot.contoso.local) check-sni nodepilot.contoso.local verifyhost nodepilot.contoso.local
```

`backup` on node B ensures HAProxy only uses B when A loses the health check. Without `backup` the
two would be round-robined and the follower would receive regular requests that it rejects with 503
(legitimate, but unnecessary logging).

Before starting:

- Place the CA chain of the backend certificates as a PEM file readable only by HAProxy at
  `/etc/haproxy/ca/nodepilot-backend-ca.pem`. Both certificates have to contain the name configured
  with `sni`/`verifyhost` as a SAN; the same name has to be in NodePilot's `AllowedHosts`. The public
  service host name is the simplest choice for that. `verify none` is not permissible even on
  internal networks.
- Configure the direct HAProxy IP on both NodePilot nodes, for example with
  `Install-NodePilot.ps1 -KnownProxyIps '10.0.1.5'`. With a redundant HAProxy pair, give both
  transport IPs. An empty trust block ignores the headers safely, but means all clients share the
  same proxy-IP rate-limit bucket.
- Validate the rendered template with `haproxy -c -f /etc/haproxy/haproxy.cfg`. The bundled static
  repository check runs through `deploy/Test-DeploymentTemplates.ps1`.

The connection settings are not just performance tuning: behind a proxy, ASP.NET Core Negotiate needs
a persistent 1:1 connection. `http-keep-alive` preserves it and `http-reuse never` prevents reuse by
a different client session. The complete template is at `deploy/templates/haproxy.cfg.template`;
further Kerberos notes are in `docs/ldap-windows-sso.md`.

## Operator runbook

### Which node is active?

```bash
curl http://nodepilot-a/healthz/leader   # 200 = active, 503 = follower
curl http://nodepilot-b/healthz/leader
```

The body on 200:
```json
{ "status": "leader", "nodeId": "nodepilot-a", "leaseExpiresAt": "...", "leaseEpoch": 7, "lastRenewAt": "..." }
```

`leaseEpoch` increases by 1 on every failover — useful for seeing how often the cluster has flipped.

### A planned failover (for example for Windows Updates on A)

```powershell
# On node A:
Stop-Service NodePilot
# Watch on node B:
curl http://nodepilot-b/healthz/leader   # answers 200 within ~10 s
# Patch A and restart it:
Start-Service NodePilot
# A becomes a follower, B stays the leader until B itself is stopped.
```

### Manually failing back to A

```powershell
Stop-Service NodePilot       # on B
# A's lease renew does not fail either, but as soon as B's lease expires (30 s after the stop)
# A acquires the lease on its next renew tick.
```

### Debugging "both nodes are followers"

Both nodes answer `503`? → the database is unreachable from both.
- Test `psql -h sql-cluster.example.com` from both nodes
- Check the database HA layer's status (the AlwaysOn dashboard, `pg_isready`, etc.)

### "The failover takes 5+ minutes"

- Check the load-balancer probe interval (it should be 5 s)
- `Cluster:LeaseTtlSeconds` is configured too high
- Database latency: if the lease UPDATE takes 30+ s, the database itself is the problem

### "Sign-in fails with 401 after a failover"

→ `Jwt:Key`, `Jwt:Issuer` or `Jwt:Audience` diverge. Open both `appsettings.Production.json` files
and make sure all three keys are exactly identical. The pre-boot validator should have caught that —
if it did not, cluster mode is accidentally off on one node.

## Backup & restore

- **The database backup is primary.** Nothing node-local is authoritative — the whole point of A/P is
  that the application database is the source of truth.
- **Do not use** the `jwt-secret.key` file on disk in a cluster — `Jwt:Key` has to be in the configuration.
- `admin-setup.token` is one-off (it only applies to an initially empty database).
- In a disaster-recovery case (the database is gone entirely, a fresh restore), both nodes have to be
  stopped before the backup is restored.

## Deliberately accepted residual risks

1. **Workflows caught mid-failover are cancelled.** There is no auto-retry. The operator clicks retry.
   A genuinely resumable engine would be several extra person-days (persisting step state mid-execution).
2. **Transient file events can remain unreconstructable.** Durable snapshots replay lasting
   create/change/delete state after failover and pair unambiguous renames. A file created and
   deleted completely while no watcher can observe it leaves no state to reconcile. Use an
   external queue/journal when every transient event matters.
3. **Quartz misfires are reconciled.** `MisfireHandlingInstructionDoNothing` prevents Quartz's own
   one-shot behavior; NodePilot replays every cron time after the durable per-trigger cursor.
4. **The database is a single point of failure.** Database HA is the operator's responsibility.
5. **No STONITH fencing.** A stale leader can still observe a source signal after a long GC pause,
   but the admission path checks the lease epoch immediately before the transactional dispatch.
   Duplicate observations converge on the unique event receipt.
6. **SignalR reconnect after a failover.** The browser SDK reconnects to the VIP automatically. The
   JWT stays valid (a shared `Jwt:Key`+`Issuer`+`Audience`).

## Cluster-mode verification checklist

- [ ] `dotnet build` succeeds on both nodes
- [ ] The database is reachable from both nodes (`psql … -c "SELECT 1"` / `sqlcmd … -Q "SELECT 1"`)
- [ ] `Jwt:Key`+`Issuer`+`Audience` are exactly identical in both `appsettings` files
- [ ] `Cluster:NodeId` differs between the nodes
- [ ] On the first start, one of the two answers `/healthz/leader` with 200 and the other with 503
- [ ] The load-balancer probe sees that within 5 s
- [ ] `Test-Failover.ps1` runs through successfully
- [ ] The audit entry `EXECUTION_RECOVERED_FAILOVER` appears after a simulated crash
