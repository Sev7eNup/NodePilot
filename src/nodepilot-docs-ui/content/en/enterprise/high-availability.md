# High availability (active/passive)

In active/passive operation, at least two NodePilot instances share one database. Exactly one instance is the leader. Only the leader executes workflows and accepts mutating API calls. Followers answer mutating endpoints with HTTP `503` and `Retry-After: 30`.

The default configuration is single node. `Cluster:Enabled=true` activates cluster operation.

## What it provides

- **An RTO of 40–60 s** on a crash: the dead leader's lease expires after at most 30 s (the TTL), the standby acquires it (renewing every 10 s), and the load balancer notices on the next 5-second probe.
- **A planned stop:** the leader releases its lease at shutdown → the standby takes over on the next 10-second tick (~10 s).
- **Fencing:** a leader that detects its own step-down (a renew returning 0 rows) immediately cancels every locally running execution.
- **A recovery sweep:** every new leader scans `WorkflowExecutions` for running rows with a foreign `OwnerNodeId` and marks them `Cancelled`.
- **`LeaseEpoch`** as a monotonic fencing token per acquire — it lands in the audit log.
- **A database write fence:** terminal engine updates are compare-and-set operations out of `Running`/`Paused`; the same SQL update checks the owner, the epoch and that the lease has not expired. An old leader cannot overwrite a `Cancelled` set by SSO offboarding.

## Configuration

```jsonc
{
  "Cluster": {
    "Enabled": false,                  // true = cluster mode
    "NodeId": null,                    // default: Environment.MachineName
    "LeaseTtlSeconds": 30,             // the lease expires after n s without a renew
    "LeaseRenewSeconds": 10,           // the leader renews every n s
    "LeaseDbTimeoutSeconds": 3         // SqlCommand.CommandTimeout for the renew
  }
}
```

**RTO formula:** `TTL + renew interval + recovery sweep` → 30 + 10 + ~5 = ~45 s worst case.

### Authentication and OIDC in a cluster

The entire `Authentication` section is config-as-code. In a cluster, `PUT /api/admin/settings/Authentication` answers with `409 CLUSTER_CONFIG_AS_CODE_REQUIRED`. All nodes have to receive the same boot-fixed authentication configuration and the same secrets; changes only take effect after a cluster restart.

OIDC additionally requires a shared, persistent ASP.NET Core data-protection key ring. Without shared keys, another node cannot decrypt the correlation, nonce or ticket data of a login flow already in progress:

```jsonc
{
  "DataProtection": {
    "KeyRingPath": "\\\\fileserver\\nodepilot\\data-protection-keys",
    "CertificateThumbprint": "<shared-certificate-thumbprint>",
    "SharedKeyRing": true
  }
}
```

The certificate with its private key has to be present in `LocalMachine\My` on every node. It may be separate from the Kestrel TLS certificate. `SharedKeyRing=true` is the explicit operator attestation that `KeyRingPath` designates the same persistent storage on all nodes.

## Implementation

- **`ClusterLeaderService`** is both a `BackgroundService` (the renew loop) and an `IClusterStateProvider` ("am I the leader?").
- Lease acquire/renew: an atomic `UPDATE … WHERE OwnerNodeId = me AND ExpiresAt > now` — two nodes cannot be leader at the same time.
- **The database clock, not the application clock:** before every lease operation, `SYSUTCDATETIME()` (SQL Server) or `now() AT TIME ZONE 'UTC'` (Postgres) is read → no split brain with diverging wall clocks.
- **`LeaderRequiredMiddleware`** blocks every mutating path on a follower with 503. Permitted: `/healthz/*`, `/openapi/*` and read-only endpoints. A `[LeaderOnly]` attribute on an endpoint wins over the path heuristic — for endpoints whose HTTP verb looks harmless but which change state (webhook ingress via `GET`).
- **The `ClusterLeader` table** with the single-row sentinel `Resource='primary'`, seeded in the `MigrationBootstrapper`.

## Health probe

```bash
curl http://nodepilot-a/healthz/leader   # 200 = leader, 503 = follower
```

Body on 200: `{ "status": "leader", "nodeId": "...", "leaseExpiresAt": "...", "leaseEpoch": 7, "lastRenewAt": "..." }`

`leaseEpoch` increases by 1 per failover.

`/healthz/ready` checks the database exclusively. The directory check is separate, at `/healthz/directory`; a DC outage must not take all HA nodes out of traffic and thereby block the local break-glass path. HAProxy still routes based on `/healthz/leader`.

## HAProxy example

```text
defaults
    mode http
    option http-keep-alive
    timeout http-keep-alive 60s

frontend nodepilot_frontend
    bind *:443 ssl crt /etc/ssl/nodepilot.pem alpn http/1.1
    http-request del-header Forwarded
    http-request del-header X-Forwarded-For
    http-request del-header X-Forwarded-Proto
    option forwardfor header X-Forwarded-For
    http-request set-header X-Forwarded-Proto https
    default_backend nodepilot_active

backend nodepilot_active
    option httpchk
    http-check send meth GET uri /healthz/leader hdr Host nodepilot.internal.example
    http-check expect status 200
    default-server inter 5s fall 2 rise 1 ssl verify required ca-file /etc/haproxy/nodepilot-backend-ca.pem alpn http/1.1
    http-reuse never
    balance source
    hash-type consistent
    server node-a 10.0.1.10:443 check sni str(nodepilot.internal.example) check-sni nodepilot.internal.example verifyhost nodepilot.internal.example
    server node-b 10.0.1.11:443 check backup sni str(nodepilot.internal.example) check-sni nodepilot.internal.example verifyhost nodepilot.internal.example
```

`http-keep-alive` together with `http-reuse never` preserves the 1:1 connection between the browser
and Kestrel that Negotiate/Kerberos requires. `verify required`, `ca-file`, SNI and `verifyhost`
prevent unvalidated TLS connections to the backends. NodePilot has to list HAProxy's transport IP
under `ForwardedHeaders:KnownProxies`; only then are the client IP/protocol headers set by the proxy
trusted for rate limits and redirects. `backup` on node B ensures HAProxy only uses B when A fails
the health check.

The complete template with all timeouts and placeholders is at
`deploy/templates/haproxy.cfg.template`. If Windows SSO is disabled, `http-reuse never` may be
relaxed to `http-reuse safe`; certificate validation and the forwarded-header trust boundary stay
unchanged.

## Operator runbook (excerpt)

**A planned failover (Windows Update on A):**
```powershell
Stop-Service NodePilot        # on A
# B answers with 200 within ~10 s
Start-Service NodePilot       # A becomes a follower, B stays the leader
```

**"Both nodes are followers"** (both 503) → the database is unreachable from both. Check database HA (`pg_isready`, the Always On dashboard).

**"An existing session returns 401 after a failover"** → `Jwt:Key`/`Issuer`/`Audience` diverge. All three have to be identical on all nodes.

**"OIDC correlation failed", or a lost OIDC login after a node change** → the data-protection key ring is not genuinely shared. Check `DataProtection:KeyRingPath`, the certificate and `SharedKeyRing` on all nodes.

## Deliberately out of scope

- **Active/active** — all mutations go through the leader.
- **Multi-region** — the lease works against exactly one database.
- **`LeaseEpoch` as a hard write-fencing column on `WorkflowExecution`** (V2) — fencing currently happens through CTS cancellation.

## Accepted residual risks

1. Workflows caught mid-failover become `Cancelled` (no auto-retry).
2. File-watcher events in the failover window are lost (`FileSystemWatcher` is process-local).
3. Quartz misfires — one cron fire per workflow can be lost in the 30–60 s window.
4. The database is a single point of failure (database HA is the operator's responsibility).
5. No STONITH — a short window (~`LeaseRenewSeconds`) with an old leader.

## Rollout recommendation

Merge with `Cluster:Enabled=false`, then enable it site by site. A single node runs identically to before with this code. In combination with `Secrets:Provider=Dpapi` (machine-bound), **the boot fails** — a cluster has to use AES-GCM (see [Secret providers](./secrets-providers)).
