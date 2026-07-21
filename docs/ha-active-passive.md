# Active/Passive HA für NodePilot

Stand: 2026-05-09. Setup für zwei NodePilot-Knoten hinter einem Load-Balancer mit
automatischem Failover (RTO ~40–60 s).

## Wann braucht man das?

- **Kunde fordert „bei Server-Crash übernimmt eine Reserve" als Compliance-Punkt.**
- Geplante Wartung (Windows Update, .NET-Patch) soll ohne Service-Unterbrechung möglich sein.
- Single-Node mit 99,9 % reicht euch nicht (8 h Downtime/Jahr).

Wenn **nichts davon zutrifft**, lasst es sein. Single-Node ist einfacher zu betreiben und
eure SLA wahrscheinlich gut genug.

## Was ihr bekommt — und was nicht

| Feature | A/P | nicht enthalten |
|---|---|---|
| Trigger-Singleton-Garantie über zwei Knoten | ✓ | — |
| Automatischer Failover bei Knoten-Crash, RTO 40–60 s | ✓ | — |
| Automatischer Failover bei DB-Verlust am Leader | ✓ | — |
| Webhooks crash-recoverable (Pending-Row + Reaper) | ✓ | — |
| Sessions überleben Failover (gleiche `Jwt:Key`+`Issuer`+`Audience`) | ✓ | — |
| Workflows mid-Failover laufen weiter | ✗ | werden als `Cancelled` markiert; Operator klickt Retry |
| Horizontale Last-Skalierung (zwei Knoten arbeiten parallel) | ✗ | das wäre Aktiv/Aktiv — eigene Stufe, separater Plan |
| DB-HA selbst | ✗ | AlwaysOn AG / Patroni / Cloud-Managed RDS — Operator-Verantwortung |
| FileWatcher-Events während Failover-Window | ✗ | sind prozesslokal, gehen verloren — wenn Atomic-Garantie nötig: Webhook-Trigger |

## Architektur

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
            │       DB-Lease (atomic UPDATE WHERE)
            └───────────────┬───────────────┘
                            ▼
              Shared SQL Server / PostgreSQL
              (HA-Layer separat: AlwaysOn AG / Patroni / RDS)
              Tabelle ClusterLeaders
```

**Failover-Sequenz** (nach Crash des Leaders A):
1. A's `ClusterLeaderService` kann seinen Lease nicht mehr renewen (DB unreachable, Prozess tot).
2. B's `ClusterLeaderService` läuft alle 10 s und sieht `ExpiresAt < db_now` → atomic `UPDATE … WHERE OwnerNodeId='' OR ExpiresAt < now`.
3. B inkrementiert `LeaseEpoch` (+1) und ist neuer Leader. `OnLeadershipAcquired` Event feuert.
4. B's `TriggerOrchestrator` startet alle Trigger-Sources (Quartz, FileWatcher, etc.).
5. B's `ClusterFailoverRecoveryHost` markiert alle `WorkflowExecutions` mit `OwnerNodeId != "nodepilot-b"` und Status `Running/Pending/Paused` als `Cancelled` mit Audit-Eintrag.
6. B's `/healthz/leader` antwortet **200** mit dem neuen `leaseEpoch`.
7. LB merkt es beim nächsten 5 s-Probe und routet Traffic auf B.

Engine-Terminalzustände werden per DB-Compare-and-Set nur aus `Running`/`Paused`
geschrieben. Im HA-Modus prüft dasselbe `UPDATE` zusätzlich `OwnerNodeId`, `LeaseEpoch`
und die noch nicht abgelaufene `ClusterLeaders`-Lease. Ein nach GC-Pause fortgesetzter alter
Leader kann daher weder ein bereits durch SSO-Offboarding gesetztes `Cancelled` überschreiben
noch nach einem Epoch-Wechsel `Succeeded`/`Failed` committen.

**Erwartete RTO: 40–60 s** (TTL 30 s + Renew-Intervall 10 s + LB-Probe 5 s).

## Voraussetzungen

- 2× Windows Server 2022 — identische `dotnet --version`. (NodePilot ist `net10.0-windows`/Windows-only — WinRM + gMSA-Kerberos; ein Linux-Node ist nicht möglich.)
- Externes SQL Server 2022 oder Postgres 16+ — eigenständige HA-Schicht
  (AlwaysOn AG / Patroni / Aurora). Diese **muss** vor NodePilot existieren; A/P löst nur
  das App-Layer-Problem, nicht das DB-Layer-Problem.
- Load-Balancer mit HTTP-Health-Probe (HAProxy, NLB, F5, AWS ALB, …).
- Ein gemeinsamer FQDN (VIP) der auf den LB zeigt.
- Identisches gMSA-Service-Account auf beiden Knoten (für WinRM-Kerberos).
- AES-GCM Secret-Master-Key generiert (geteilt über beide Knoten) — DPAPI ist im Cluster nicht zulässig (siehe Abschnitt unten).

## Konfiguration

```jsonc
// appsettings.Production.json — auf Knoten A
{
  "Cluster": {
    "Enabled": true,
    "NodeId": "nodepilot-a",          // Knoten B: "nodepilot-b"
    "LeaseTtlSeconds": 30,
    "LeaseRenewSeconds": 10,
    "LeaseDbTimeoutSeconds": 3
  },
  "Jwt": {
    "Key": "<base64-32-bytes-shared>", // PFLICHT — auto-generated jwt-secret.key würde divergieren
    "Issuer": "NodePilot-Prod",        // PFLICHT — beide Knoten müssen denselben String setzen
    "Audience": "NodePilot-Prod"       // PFLICHT — dito
  },
  "Database": { "Provider": "sqlserver" },
  "ConnectionStrings": {
    "DefaultConnection": "Server=sql-cluster.firma.de;Database=NodePilot;Trusted_Connection=True;Encrypt=True"
  },
  "Secrets": {
    "Provider": "AesGcm",                 // PFLICHT im Cluster — DPAPI wird beim Boot abgelehnt
    "MasterKey": "<base64-32-bytes-shared>" // identisch auf beiden Knoten (via Secrets__MasterKey env var)
  }
}
```

**Boot-Validierung:** Bei `Cluster:Enabled=true` schlägt der Service-Start fehl wenn
`Jwt:Key`, `Jwt:Issuer` oder `Jwt:Audience` fehlen oder leer sind — **und** wenn
`Secrets:Provider` auf `Dpapi` steht (oder fehlt): DPAPI-Ciphertexte sind host-gebunden,
ein Standby könnte sie nach Failover nicht entschlüsseln, daher erzwingt
`SecretProtectorBootstrapFactory` hier `Secrets:Provider=AesGcm` + `Secrets:MasterKey`.
Klare Fehlermeldung nennt den fehlenden bzw. inkompatiblen Key.

## Konfiguration-Optionen im Detail

| Key | Default | Wirkung |
|---|---|---|
| `Cluster:Enabled` | `false` | Master-Switch. False = Single-Node (no-op state provider). |
| `Cluster:NodeId` | `Environment.MachineName` | Identifikation für Lease, Audit, Recovery. Pflicht bei Container-Hashes. |
| `Cluster:LeaseTtlSeconds` | `30` | Wie lange ein Lease gültig ist. Niedriger = schnellerer Failover, aber empfindlicher gegen DB-Hickups. |
| `Cluster:LeaseRenewSeconds` | `10` | Renew-Intervall. Sollte ≤ TTL/3 sein. |
| `Cluster:LeaseDbTimeoutSeconds` | `3` | Command-Timeout für Lease-Queries. Niedrig damit ein hängender DB-Renew nicht den BackgroundService blockiert. |
| `Jwt:Key` | (auto-gen) | **Pflicht im Cluster.** Base64-encoded, ≥32 bytes. |
| `Jwt:Issuer` | `"NodePilot"` | **Pflicht im Cluster.** Beide Knoten müssen denselben String setzen. |
| `Jwt:Audience` | `"NodePilot"` | **Pflicht im Cluster.** Dito. |

## Secret-Verschlüsselung im Cluster

DPAPI-`LocalMachine` ist **machine-bound**. Knoten B kann eine von Knoten A verschlüsselte
Credential nicht entschlüsseln. Das betrifft sowohl `Credentials` als auch
**Secret-Global-Variables** (beide laufen über denselben aktiven `ISecretProtector`).

Deshalb **erzwingt der Code AES-GCM im Cluster**: `SecretProtectorBootstrapFactory` lehnt
`Cluster:Enabled=true` + `Secrets:Provider=Dpapi` (oder fehlenden `Secrets:Provider`, der
auf `Dpapi` defaultet) beim Boot ab. Es gibt im Cluster nur einen unterstützten Weg:

- **AES-GCM mit shared Master-Key** (`Secrets:Provider=AesGcm`, `Secrets:MasterKey` =
  base64-encodierte 32 Bytes, **identisch auf beiden Knoten**, via `Secrets__MasterKey`
  env var ausgeliefert). Beide Stores (Credentials + Secret-Globals) sind damit auf jedem
  Knoten les-/schreibbar; kein manuelles Re-Entry nötig. Key-Erzeugung + Operator-Runbook:
  `docs/secrets-providers.md`.

Bestehende Single-Node-Instanzen mit DPAPI-verschlüsselten Secrets vor dem Cluster-Switch
einmalig auf AES-GCM umschlüsseln (`POST /api/secrets/reencrypt`, siehe
`docs/secrets-providers.md`).

## Installation

Identisch zu Single-Node bis auf:
1. SQL Server / Postgres bereitstellen + HA-Layer aktivieren (AlwaysOn AG / Patroni).
2. Knoten A installieren via `Install-NodePilot.ps1`. Cluster-Block in `appsettings.Production.json` ergänzen.
3. `Jwt:Key` einmal mit einem CSPRNG generieren (`$r=[Security.Cryptography.RandomNumberGenerator]::Create();$b=New-Object byte[] 32;try{$r.GetBytes($b);[Convert]::ToBase64String($b)}finally{$r.Dispose();[Array]::Clear($b,0,$b.Length)}`).
4. Knoten B identisch installieren — gleiche `Jwt:Key`, **andere** `Cluster:NodeId`.
5. LB konfigurieren (HAProxy-Beispiel unten).
6. Smoke-Test: `curl http://nodepilot-a/healthz/leader` → 200, `curl http://nodepilot-b/healthz/leader` → 503 (oder umgekehrt — wer als erster startet wird Leader).
7. `deploy/Test-Failover.ps1` ausführen.

## HAProxy-Beispiel

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

`backup` auf Knoten B sorgt dafür dass HAProxy nur dann B nutzt, wenn A den Health-Check
verliert. Ohne `backup` würden beide round-robin'd und der Follower bekäme regelmäßige
Anfragen die er mit 503 zurückwirft (legitim, aber unnötiges Logging).

Vor dem Start:

- Die CA-Kette der Backend-Zertifikate als nur für HAProxy lesbare PEM-Datei unter
  `/etc/haproxy/ca/nodepilot-backend-ca.pem` ablegen. Beide Zertifikate müssen den mit
  `sni`/`verifyhost` konfigurierten Namen als SAN enthalten; derselbe Name muss in
  NodePilots `AllowedHosts` stehen. Der öffentliche Service-Hostname ist dafür die
  einfachste Wahl. `verify none` ist auch in
  internen Netzen nicht zulässig.
- Auf beiden NodePilot-Knoten die direkte HAProxy-IP konfigurieren, zum Beispiel mit
  `Install-NodePilot.ps1 -KnownProxyIps '10.0.1.5'`. Bei einem redundanten HAProxy-Paar
  beide Transport-IPs angeben. Ein leerer Trust-Block ignoriert die Header sicher, führt
  aber dazu, dass alle Clients denselben Proxy-IP-Rate-Limit-Bucket teilen.
- Das gerenderte Template mit `haproxy -c -f /etc/haproxy/haproxy.cfg` validieren. Der
  mitgelieferte statische Repository-Check läuft über `deploy/Test-DeploymentTemplates.ps1`.

Die Verbindungseinstellungen sind nicht nur Performance-Tuning: ASP.NET Core Negotiate
benötigt hinter einem Proxy eine persistente 1:1-Verbindung. `http-keep-alive` erhält sie,
`http-reuse never` verhindert die Wiederverwendung durch eine andere Client-Session. Das
vollständige Template liegt unter `deploy/templates/haproxy.cfg.template`; weitere
Kerberos-Hinweise stehen in `docs/ldap-windows-sso.md`.

## Operator-Runbook

### Welcher Knoten ist aktiv?

```bash
curl http://nodepilot-a/healthz/leader   # 200 = aktiv, 503 = follower
curl http://nodepilot-b/healthz/leader
```

Body bei 200:
```json
{ "status": "leader", "nodeId": "nodepilot-a", "leaseExpiresAt": "...", "leaseEpoch": 7, "lastRenewAt": "..." }
```

`leaseEpoch` springt um 1 bei jedem Failover — nützlich um zu sehen wie oft das Cluster
geflippt ist.

### Geplantes Failover (z. B. für Windows Updates auf A)

```powershell
# Auf Knoten A:
Stop-Service NodePilot
# Auf Knoten B beobachten:
curl http://nodepilot-b/healthz/leader   # antwortet 200 innerhalb ~10 s
# A patchen, neu starten:
Start-Service NodePilot
# A wird Follower, B bleibt Leader bis B selbst gestoppt wird.
```

### Manuelles Failover zurück auf A

```powershell
Stop-Service NodePilot       # auf B
# A's Lease-Renew schlägt zwar auch nicht fehl, aber sobald B's Lease abläuft (30 s nach Stop)
# bekommt A die Lease beim nächsten Renew-Tick.
```

### „Beide Knoten Follower" debuggen

Beide Knoten antworten `503`? → DB ist von beiden nicht erreichbar.
- `psql -h sql-cluster.firma.de` von beiden Knoten testen
- DB-HA-Layer-Status prüfen (AlwaysOn-Dashboard, `pg_isready`, etc.)

### „Failover dauert 5+ Minuten"

- LB-Probe-Intervall prüfen (sollte 5 s sein)
- `Cluster:LeaseTtlSeconds` zu hoch konfiguriert
- DB-Latenz: wenn Lease-UPDATE 30+ s braucht, ist die DB selbst das Problem

### „Login nach Failover schlägt mit 401 fehl"

→ `Jwt:Key`, `Jwt:Issuer` oder `Jwt:Audience` divergieren. Beide
`appsettings.Production.json` öffnen, sicherstellen dass alle drei Keys exakt identisch sind.
Pre-Boot-Validator hätte das eigentlich abfangen müssen — wenn nicht, dann ist auf einem
Knoten der Cluster-Mode versehentlich aus.

## Backup & Restore

- **DB-Backup ist primär.** Nichts knoten-lokales ist authoritativ — der ganze Sinn von A/P
  ist dass die App-DB die Source of Truth ist.
- `jwt-secret.key`-File auf Disk **nicht verwenden** im Cluster — `Jwt:Key` muss in der Config stehen.
- `admin-setup.token` einmalig (gilt nur für initiale leere DB).
- Im Disaster-Recovery-Fall (DB komplett weg, frische Wiederherstellung) müssen beide
  Knoten gestoppt sein bevor das Backup zurückgespielt wird.

## Bewusst akzeptierte Restrisiken

1. **Workflows mid-Failover = Cancelled.** Kein Auto-Retry. Operator klickt Retry. Echte
   resumable Engine wäre Multi-PT extra (Step-State-Persistierung mid-execution).
2. **FileWatcher-Events während Failover-Window verloren.** `FileSystemWatcher` ist prozesslokal.
   Wenn Atomic-Garantie nötig: externe Queue + Webhook-Trigger.
3. **Quartz-Misfires.** 1 Cron-Fire pro Workflow im 30–60s Failover-Window kann verloren gehen
   (`MisfireHandlingInstructionDoNothing`). Backfill-Storm wäre schlimmer.
4. **DB ist Single Point of Failure.** DB-HA ist Operator-Verantwortung.
5. **Kein STONITH-Fencing.** Bei langer GC-Pause könnte alter Leader für ~10 s noch
   fire'n. Mitigation: defensiver `IsLeader`-Check in `FireAsync`. Worst-Case-Fenster
   = `LeaseRenewSeconds`.
6. **SignalR-Reconnect nach Failover.** Browser-SDK reconnected automatisch zur VIP. JWT
   bleibt valide (shared `Jwt:Key`+`Issuer`+`Audience`).

## Cluster-Mode Verifikations-Checkliste

- [ ] `dotnet build` erfolgreich auf beiden Knoten
- [ ] DB erreichbar von beiden Knoten (`psql … -c "SELECT 1"` / `sqlcmd … -Q "SELECT 1"`)
- [ ] `Jwt:Key`+`Issuer`+`Audience` exakt identisch in beiden `appsettings`
- [ ] `Cluster:NodeId` unterschiedlich auf den Knoten
- [ ] Beim ersten Start: einer der beiden antwortet `/healthz/leader` mit 200, der andere mit 503
- [ ] LB-Probe sieht das innerhalb 5 s
- [ ] `Test-Failover.ps1` läuft erfolgreich durch
- [ ] Audit-Eintrag `EXECUTION_RECOVERED_FAILOVER` taucht nach simuliertem Crash auf
