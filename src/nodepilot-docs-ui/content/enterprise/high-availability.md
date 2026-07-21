# High Availability (Active/Passive)

Zwei (oder mehr) NodePilot-Instanzen teilen sich **eine** Datenbank. Exakt eine ist **Leader** und akzeptiert mutierende API-Calls + führt Workflows aus; alle anderen sind **Followers** und antworten auf mutierende Endpoints mit `503` + `Retry-After: 30`.

Default: **Single-Node (off)**. Aktivierung via `Cluster:Enabled=true`.

## Was es liefert

- **RTO 40–60 s** bei Crash: Lease des toten Leaders expired nach max 30 s (TTL), Standby akquiriert (Renew alle 10 s), LB bemerkt beim nächsten 5-s-Probe.
- **Geplanter Stop:** Leader released Lease beim Shutdown → Standby übernimmt am nächsten 10-s-Tick (~10 s).
- **Fencing:** Ein Leader, der seinen eigenen Step-down erkennt (Renew returned 0 Rows), cancelt sofort alle lokal laufenden Executions.
- **Recovery-Sweep:** Jeder neue Leader scannt `WorkflowExecutions` auf Running-Rows mit fremdem `OwnerNodeId` und markiert sie `Cancelled`.
- **LeaseEpoch** als monotoner Fencing-Token pro Acquire — landet im Audit-Log.
- **DB-Write-Fence:** Terminale Engine-Updates sind Compare-and-Set-Operationen aus `Running`/`Paused`; dasselbe SQL-Update prüft Owner, Epoch und nicht abgelaufene Lease. Ein alter Leader kann ein durch SSO-Offboarding gesetztes `Cancelled` nicht zurücküberschreiben.

## Konfiguration

```jsonc
{
  "Cluster": {
    "Enabled": false,                  // true = cluster mode
    "NodeId": null,                    // Default: Environment.MachineName
    "LeaseTtlSeconds": 30,             // Lease expired nach n s ohne Renew
    "LeaseRenewSeconds": 10,           // Leader erneuert alle n s
    "LeaseDbTimeoutSeconds": 3         // SqlCommand.CommandTimeout für Renew
  }
}
```

**RTO-Formel:** `TTL + Renew-Interval + Recovery-Sweep` → 30 + 10 + ~5 = ~45 s worst case.

### Authentication und OIDC im Cluster

Die gesamte `Authentication`-Sektion ist Config-as-Code. `PUT /api/admin/settings/Authentication` antwortet im Cluster mit `409 CLUSTER_CONFIG_AS_CODE_REQUIRED`. Alle Nodes müssen dieselbe boot-feste Authentication-Konfiguration und dieselben Secrets erhalten; Änderungen werden erst nach Cluster-Neustart aktiv.

OIDC benötigt zusätzlich einen gemeinsamen persistenten ASP.NET-Core-Data-Protection-Keyring. Ohne gemeinsame Keys kann ein anderer Node Correlation-, Nonce- oder Ticketdaten eines begonnenen Loginflows nicht entschlüsseln:

```jsonc
{
  "DataProtection": {
    "KeyRingPath": "\\\\fileserver\\nodepilot\\data-protection-keys",
    "CertificateThumbprint": "<shared-certificate-thumbprint>",
    "SharedKeyRing": true
  }
}
```

Das Zertifikat mit Private Key muss auf jedem Node in `LocalMachine\My` vorhanden sein. Es darf vom Kestrel-TLS-Zertifikat getrennt sein. `SharedKeyRing=true` ist die explizite Operator-Attestation, dass `KeyRingPath` auf allen Nodes denselben persistenten Storage bezeichnet.

## Implementierung

- **`ClusterLeaderService`** ist `BackgroundService` (Renew-Loop) und `IClusterStateProvider` ("am I leader?").
- Lease-Acquire/Renew: atomares `UPDATE … WHERE OwnerNodeId = me AND ExpiresAt > now` — zwei Nodes können nicht gleichzeitig Leader sein.
- **DB-Clock, nicht App-Clock:** vor jeder Lease-Operation wird `SYSUTCDATETIME()` (SQL Server) bzw. `now() AT TIME ZONE 'UTC'` (Postgres) gelesen → kein Split-Brain bei divergenten Wall-Clocks.
- **`LeaderRequiredMiddleware`** blockt jeden mutierenden Pfad auf einem Follower mit 503. Erlaubt: `/healthz/*`, `/openapi/*`, Read-Only-Endpoints.
- **`ClusterLeader`**-Tabelle mit Single-Row-Sentinel `Resource='primary'`, geseedet im `MigrationBootstrapper`.

## Health-Probe

```bash
curl http://nodepilot-a/healthz/leader   # 200 = Leader, 503 = Follower
```

Body bei 200: `{ "status": "leader", "nodeId": "...", "leaseExpiresAt": "...", "leaseEpoch": 7, "lastRenewAt": "..." }`

`leaseEpoch` springt pro Failover um 1.

`/healthz/ready` prüft ausschließlich die DB. Der Directory-Check liegt separat unter `/healthz/directory`; ein DC-Ausfall darf nicht alle HA-Nodes aus dem Traffic nehmen und dadurch den lokalen Break-Glass-Pfad blockieren. HAProxy routet weiterhin anhand von `/healthz/leader`.

## HAProxy-Beispiel

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

`http-keep-alive` zusammen mit `http-reuse never` bewahrt die für Negotiate/Kerberos nötige
1:1-Verbindung zwischen Browser und Kestrel. `verify required`, `ca-file`, SNI und
`verifyhost` verhindern unvalidierte TLS-Verbindungen zu den Backends. NodePilot muss die
Transport-IP von HAProxy unter `ForwardedHeaders:KnownProxies` führen; nur dann werden die
vom Proxy neu gesetzten Client-IP-/Protokoll-Header für Rate-Limits und Redirects vertraut.
`backup` auf Node B stellt sicher, dass HAProxy B nur nutzt, wenn A die Health-Check verfehlt.

Die vollständige Vorlage mit allen Timeouts und Platzhaltern liegt unter
`deploy/templates/haproxy.cfg.template`. Wenn Windows-SSO deaktiviert ist, darf
`http-reuse never` zu `http-reuse safe` gelockert werden; Zertifikatsprüfung und
Forwarded-Header-Trust-Boundary bleiben unverändert.

## Operator-Runbook (Auszug)

**Geplanter Failover (Windows Update auf A):**
```powershell
Stop-Service NodePilot        # auf A
# B antwortet innerhalb ~10 s mit 200
Start-Service NodePilot       # A wird Follower, B bleibt Leader
```

**"Beide Nodes Follower"** (beide 503) → DB von beiden unreachable. DB-HA prüfen (`pg_isready`, AlwaysOn-Dashboard).

**"Bestehende Session liefert 401 nach Failover"** → `Jwt:Key`/`Issuer`/`Audience` divergieren. Alle drei müssen auf allen Nodes identisch sein.

**"OIDC correlation failed" oder verlorener OIDC-Login nach Node-Wechsel** → der Data-Protection-Keyring ist nicht wirklich gemeinsam. `DataProtection:KeyRingPath`, Zertifikat und `SharedKeyRing` auf allen Nodes prüfen.

## Deliberately out of scope

- **Active/Active** — alle Mutationen gehen durch den Leader.
- **Multi-Region** — Lease arbeitet gegen exakt eine DB.
- **LeaseEpoch als harte Write-Fencing-Spalte auf `WorkflowExecution`** (V2) — aktuell Fencing via CTS-Cancellation.

## Akzeptierte Restrisiken

1. Workflows mid-Failover = `Cancelled` (kein Auto-Retry).
2. FileWatcher-Events im Failover-Fenster gehen verloren (`FileSystemWatcher` ist prozess-lokal).
3. Quartz-Misfires — 1 Cron-Fire pro Workflow im 30–60s-Fenster kann verloren gehen.
4. DB ist Single Point of Failure (DB-HA = Operator-Verantwortung).
5. Kein STONITH — kurzes Fenster (~`LeaseRenewSeconds`) mit altem Leader.

## Rollout-Empfehlung

Merge mit `Cluster:Enabled=false`, dann pro Site einzeln enablen. Ein Single-Node läuft mit dem Code identisch zu vorher. In Kombination mit `Secrets:Provider=Dpapi` (machine-bound) **failt der Boot** — im Cluster muss AES-GCM verwendet werden (siehe [Secret-Provider](./secrets-providers)).
