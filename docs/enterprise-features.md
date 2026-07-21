# NodePilot Enterprise Features

NodePilot bündelt HA, Secret-Provider, SIEM, Folder-RBAC und Enterprise-Authentifizierung.
Die externen Authentifizierungs- und Provisioning-Pfade sind opt-in. Der SSO-Status bleibt
bis zum bestandenen realen AD-/Kerberos-/LDAPS-Feldtest ausdrücklich **AD SSO Preview**.

| Feature | Branch | Status | Default | Config-Switch |
|---|---|---|---|---|
| HA Active/Passive | `feat/ha-active-passive` (gemerged) | implementiert + Field-Test | Single-Node (off) | `Cluster:Enabled=true` |
| Vault / Secret-Provider | `feat/vault-secrets-abstraction` (gemerged) | implementiert + Field-Test | DPAPI | `Secrets:Provider=aesgcm` |
| SIEM-Logging (ECS-JSON) | `feat/siem-logging` (gemerged) | implementiert | text | `Logging:Format=ecs-json` |
| RBAC Stufe A (Shared Folders) | `feat/rbac-shared-folders` (gemerged) | implementiert | aktiv (alle bestehenden User auf Root) | — |
| AD SSO | `feature/enterprise-sso-hardening` | **Preview**, realer Feldtest offen | off | `Authentication:Ldap:Enabled` / `Authentication:Windows:Enabled` |
| OIDC + SCIM 2.0 | `feature/enterprise-sso-hardening` | implementiert, separates Release-Gate | off | `Authentication:Oidc:Enabled` / `Authentication:Scim:Enabled` |

Die Infrastruktur-Features bleiben unabhängig aktivierbar. Alle Login-Pfade konvergieren
jedoch bewusst auf dasselbe Identitäts-, Session-, Membership- und Offboarding-Modell.

---

## 1. High Availability (Active/Passive)

### Was es kann

- Zwei (oder mehr) NodePilot-Instanzen teilen sich **eine** Datenbank. Genau eine ist zu
  jedem Zeitpunkt **Leader** und akzeptiert mutierende API-Calls + führt Workflows aus;
  alle anderen sind **Follower** und antworten auf mutierende Endpoints mit `503` +
  `Retry-After: 30`.
- **RTO 40–60 Sekunden** bei einem Crash: die Lease des toten Leaders läuft nach maximal
  30 s (TTL) ab, der Standby acquired (Renew-Loop alle 10 s) und der LB merkt es beim
  nächsten 5-s-Probe (≈ TTL + Renew + Probe). Bei einem **geplanten** Stop gibt der Leader
  seine Lease beim Shutdown aktiv frei (`ClusterLeaderService.StopAsync`), sodass der
  Standby auf dem nächsten 10-s-Tick übernimmt → ~10 s.
- **Fencing**: ein Leader, der sich selbst step-down erkennt (Renew lieferte 0 Rows),
  cancelt sofort alle lokal laufenden Workflow-Executions, damit der neue Leader die
  orphan rows ohne Write-Race adoptieren kann.
- **Recovery-Sweep**: jeder neue Leader scannt beim Acquire-Event `WorkflowExecutions`
  nach Running-Rows, die einer fremden `OwnerNodeId` gehören, und markiert sie als
  `Cancelled` — keine Zombie-Runs nach Failover.
- **LeaseEpoch** als monotonisches Fencing-Token in jedem Acquire — landet im Audit, sodass
  Post-Mortems erkennen können „dies war Leader-Inkarnation 7, danach 8".
- **Terminal-Write-Fence**: Engine-Abschlüsse schreiben per Compare-and-Set nur aus
  `Running`/`Paused`; im HA-Modus prüft dasselbe DB-Update Owner, Epoch und Lease-Ablauf.
  Ein alter Leader kann dadurch ein SSO-Offboarding-`Cancelled` nicht überschreiben.

### Wie es umgesetzt ist

- **`ClusterLeaderService`** (`src/NodePilot.Scheduler/Cluster/`) ist gleichzeitig
  `BackgroundService` (treibt den Renew-Loop) und `IClusterStateProvider` (alle anderen
  Komponenten lesen darüber „bin ich Leader?").
- Lease-Acquire/Renew als atomares `UPDATE ... WHERE OwnerNodeId = me AND ExpiresAt > now`
  — zwei Nodes können nicht gleichzeitig glauben, sie wären Leader.
- **DB-Clock statt App-Clock**: vor jeder Lease-Operation liest der Service `SYSUTCDATETIME()`
  (SQL Server) bzw. `(now() AT TIME ZONE 'UTC')` (Postgres), damit zwei Nodes mit
  abweichenden Wall-Clocks nicht in einen Split-Brain laufen.
- **`LeaderRequiredMiddleware`** (`src/NodePilot.Api/Security/`) blockt jeden mutierenden
  Pfad auf einem Follower mit 503. Erlaubt: `/healthz/*`, `/openapi/*`, read-only Endpoints.
  Defense-in-Depth — der Loadbalancer sollte Follower eh nicht ansprechen.
- **`ClusterFailoverRecoveryHost`** subscribed im **Constructor** (nicht in `StartAsync`,
  damit das erste Acquire-Event nicht in eine leere Handler-Liste feuert) auf
  `OnLeadershipAcquired` und ruft `StartupRecovery.RecoverOrphanedExecutionsAsync`.
- **`ClusterFencingHost`** subscribed auf `OnLeadershipLost` und triggert
  `WorkflowEngine.CancelAllLocalAsync()` — eine **statische** Methode, weil
  `_runningExecutions` process-static ist; der Singleton-Host braucht keine scoped Engine.
- **`ClusterLeader`-Tabelle** mit Single-Row-Sentinel `Resource='primary'`. Seed im
  `MigrationBootstrapper` (Runtime, nach `Migrate()` — `SeedClusterLeaderRow`, **nicht** als
  Migration-`HasData`); Boot-Race zweier Nodes auf den Insert wird mit `try/catch DbUpdateException`
  + Re-Query abgefangen — nach dem Catch wird geprüft, ob die Row jetzt existiert. Wenn ja
  = benigner Race (still loggen), wenn nein = echter DB-/Permission-/Schemafehler (rethrow,
  Boot fail loudly). Verhindert dass Permission-Errors als „Race" verschluckt werden.

### Konfiguration

```jsonc
{
  "Cluster": {
    "Enabled": false,                  // true = Cluster-Modus
    "NodeId": null,                    // Default: Environment.MachineName
    "LeaseTtlSeconds": 30,             // Lease läuft nach n s ohne Renew ab
    "LeaseRenewSeconds": 10,           // Leader renewed alle n s
    "LeaseDbTimeoutSeconds": 3         // SqlCommand.CommandTimeout für Renew
  }
}
```

**Sizing-Daumenregel:** RTO ≈ TTL + Renew-Interval + Recovery-Sweep-Dauer.
TTL=30s + Renew=10s + Sweep=~5s → ~45s Worst Case.

### Wichtige Dateien

- [src/NodePilot.Scheduler/Cluster/ClusterLeaderService.cs](../src/NodePilot.Scheduler/Cluster/ClusterLeaderService.cs)
- [src/NodePilot.Engine/Cluster/SingleNodeClusterStateProvider.cs](../src/NodePilot.Engine/Cluster/SingleNodeClusterStateProvider.cs)
- [src/NodePilot.Api/Hosting/ClusterSetup.cs](../src/NodePilot.Api/Hosting/ClusterSetup.cs)
- [src/NodePilot.Api/Hosting/ClusterFailoverRecoveryHost.cs](../src/NodePilot.Api/Hosting/ClusterFailoverRecoveryHost.cs)
- [src/NodePilot.Api/Hosting/ClusterFencingHost.cs](../src/NodePilot.Api/Hosting/ClusterFencingHost.cs)
- [src/NodePilot.Api/Security/LeaderRequiredMiddleware.cs](../src/NodePilot.Api/Security/LeaderRequiredMiddleware.cs)
- [src/NodePilot.Engine/Execution/StartupRecovery.cs](../src/NodePilot.Engine/Execution/StartupRecovery.cs)

### Bewusst nicht in Scope

- **Active/Active** — alle Mutations laufen über den Leader. Active/Active braucht
  Konflikt-Resolution auf jedem mutierenden Endpoint (Workflow-Lock, Execution-Recovery,
  Audit-Sequenz) und ist eine eigene Engineering-Etage.
- **Multi-Region** — die Lease arbeitet gegen genau eine DB. Cross-Region setzt
  Geo-Replication + Konfliktdetektion voraus.
- **LeaseEpoch auf WorkflowExecution** als hartes Write-Fencing (V2): aktuell wird
  Fencing über CTS-Cancellation der laufenden Executions gemacht; eine harte Epoch-Spalte
  würde DB-Side rejecten, dass ein alter Leader rows updated.

### Field-Test

```powershell
# 1. Postgres laufen lassen
& 'C:\NodePilot-Postgres\pgsql\bin\pg_ctl.exe' start -D 'C:\NodePilot-Postgres\data' -w

# 2. Zwei Instanzen mit Cluster:Enabled=true starten (verschiedene Ports)
$env:Cluster__Enabled='true'; $env:Cluster__NodeId='node-a'
dotnet run --project src/NodePilot.Api --urls http://localhost:5000

# In zweitem Terminal
$env:Cluster__Enabled='true'; $env:Cluster__NodeId='node-b'
dotnet run --project src/NodePilot.Api --urls http://localhost:5001

# 3. Leader feststellen: GET /healthz/leader → 200 für Leader, 503 für Follower
curl http://localhost:5000/healthz/leader
curl http://localhost:5001/healthz/leader

# 4. Leader killen, Stoppuhr starten, bis curl gegen :5001 wieder 200 liefert
```

Erwartung: 40–60 s bis `/healthz/leader` auf node-b grün wird. Audit-Log zeigt
`LeaseEpoch` monoton steigend (1 → 2).

---

## 2. Vault / Pluggable Secret Provider

### Was es kann

- Verschlüsselt **Credentials** und **Global Variables** at rest. Bisher hart an Windows
  DPAPI gekoppelt; das Feature führt eine Provider-Abstraktion ein und liefert eine
  zweite Implementierung gegen AES-GCM mit Key aus Env-Variable.
- **Provider-Migration** über einen `MigratingSecretProtector`-Wrapper: für die Dauer
  der Rotation läuft ein zweiter (Legacy-)Provider parallel. Reads probieren Active
  zuerst, fallen auf Legacy zurück; Writes nutzen immer Active. Ein admin-getriggerter
  Bulk-Sweep (`POST /api/secrets/reencrypt`) zieht jede Row durch Decrypt→Encrypt und
  beendet das Migration-Fenster. Skipped Rows (z.B. korrupte Ciphertexte) werden im
  Response namentlich gelistet; HTTP `207 Multi-Status` signalisiert „nicht alles
  migriert", `200 OK` nur bei sauberem Cutover.
- **HA-Guardrail**: `Cluster:Enabled=true` + `Secrets:Provider=Dpapi` (oder Default-leer)
  schmiert beim Boot ab — DPAPI ist machine-bound, der Standby könnte nie dechiffrieren
  was der Leader schreibt. Hart-Fail statt silent-broken-Cluster.
- **Provider-Typo Hard-Fail**: unbekannte `Secrets:Provider`-Werte (z.B. `AesGCMm`) werden
  am Boot verworfen, kein silent-Fallback auf DPAPI mehr.
- **Fail-Loud Globals**: wenn ein Workflow `{{globals.STRIPE_KEY}}` referenziert und
  STRIPE_KEY zwar in der DB existiert aber nicht entschlüsselt werden kann (Scope-
  Mismatch, Key-Rotation ohne Sweep), failt der Workflow **vor dem ersten Step** mit
  klarer Fehlermeldung — kein silent-Substitute des Literals in HTTP-Header.
- **Audit der Crypto-Operationen** über Metrics: `nodepilot_credential_crypto_calls{operation,result}`
  unterscheidet `encrypt`/`decrypt` × `success`/`failure`. `nodepilot_credential_crypto_legacy_reads`
  zählt Decrypts die vom Legacy-Provider (Migrations-Window) bedient wurden — wenn der
  Counter auf null ist, kann der Operator das Legacy-Config sicher wegwerfen.

### Wie es umgesetzt ist

- **`ISecretProtector`** (`src/NodePilot.Core/Interfaces/`) — minimale Schnittstelle
  `Protect(byte[]) → byte[]`, `Unprotect(byte[]) → byte[]`, `Name`. Stateless, threadsafe.
- **`DpapiSecretProtector`** — Default. Liest `Credentials:DpapiScope` (`CurrentUser` |
  `LocalMachine`); `LocalMachine` ist Production-Empfehlung weil Service-Account-Wechsel
  überlebt werden.
- **`AesGcmSecretProtector`** — 32-Byte-Key wird Base64-codiert aus dem Config-Key
  `Secrets:MasterKey` gelesen (typischerweise via Env-Var `Secrets__MasterKey`, nicht im
  `appsettings.json`). 96-Bit-Random-Nonce pro Verschlüsselung, 128-Bit-Tag
  für Integrität — Format-Header markiert `nodepilot-aesgcm-v1` damit künftige
  Algorithmus-Wechsel ohne DB-Sweep gehen.
- **`SecretProtectorRegistry`** wählt beim Boot anhand `Secrets:Provider`:
  - Ohne `Secrets:LegacyProvider` → genau eine Implementierung wird als `ISecretProtector`
    in DI hinterlegt.
  - Mit `Secrets:LegacyProvider` → die aktive wird in `MigratingSecretProtector` gewickelt,
    der Reads zuerst über Active, dann über Legacy versucht. Writes immer über Active.
  - DPAPI-Scope-Werte werden über `DpapiScopeResolver.Parse` hart validiert — sowohl für
    `Credentials:DpapiScope` als auch `Secrets:LegacyDpapiScope`. Tippfehler wie
    `Local_Machine` schmieren ab statt still auf `CurrentUser` zu fallen.
- **`MigratingSecretProtector`** ist ein dünner Decorator. Bei Decrypt-Failure unter der
  aktiven Implementierung greift die Legacy-Implementierung; bleibt das Plaintext leer,
  wird ein kombinierter `CryptographicException`-Diagnostic geworfen, der beide Versuche
  benennt.
- **`POST /api/secrets/reencrypt`** (Admin-only) liest jede Credential + jede Secret-
  Global-Variable, dechiffriert über den (ggf. wrappenden) Protector, re-enkryptiert
  unter dem Active-Provider und schreibt zurück. Skipped Rows landen mit `(id, name, reason)`
  im Response.
- **DI-Disambiguierung über `[ActivatorUtilitiesConstructor]`**: `CredentialStore` und
  `GlobalVariableStore` haben mehrere Konstruktoren (Legacy + neuer Single-Arg-Pfad mit
  Protector). Microsoft.Extensions.DependencyInjection würde sonst mit
  `AmbiguousMatchException` werfen. Mit dem Attribut wird der „richtige" Konstruktor
  explizit markiert.

### Konfiguration

```jsonc
{
  "Credentials": {
    "DpapiScope": "LocalMachine"            // CurrentUser | LocalMachine (DPAPI-Pfad)
  },
  "Secrets": {
    "Provider": "Dpapi",                    // "Dpapi" (default) | "AesGcm"
    "MasterKey": null,                      // base64-encodierte 32 Bytes — Pflicht für AesGcm

    // Optional, nur während einer Provider-Rotation gesetzt: der alte Provider wird
    // als Read-Fallback gewickelt, damit der Bulk-Re-Encrypt-Sweep alte Rows lesen kann.
    "LegacyProvider": null,                 // "Dpapi" | "AesGcm" (oder leer)
    "LegacyDpapiScope": null,               // CurrentUser | LocalMachine (für Legacy=Dpapi)
    "LegacyMasterKey": null                 // base64 — für Legacy=AesGcm (Master-Key-Rotation)
  }
}
```

```powershell
# Key generieren (32 Bytes random, Base64-codiert)
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$keyBytes = New-Object byte[] 32
try {
    $rng.GetBytes($keyBytes)
    [Convert]::ToBase64String($keyBytes)
} finally {
    $rng.Dispose()
    [Array]::Clear($keyBytes, 0, $keyBytes.Length)
}
```

Den Key-Wert dann ablegen wo immer der Service-Account ihn ausgehändigt bekommt — z.B.
Windows-Service `RegistryKey` `HKLM\SYSTEM\CurrentControlSet\Services\NodePilot\Environment`
mit MULTI_SZ-Wert `Secrets__MasterKey=<base64>` (mappt auf den Config-Key
`Secrets:MasterKey`). **Nie ins Repo committen**, nie ins
Logfile loggen — `SecretProtectorRegistry` startet mit einer Hardening-Warning wenn der
Key im Klartext im `appsettings.json` steht.

### API-Surface

| Endpoint | Auth | Zweck |
|---|---|---|
| `POST /api/secrets/reencrypt` | Admin | Bulk-Sweep aller Credentials + Secret-Globals durch Decrypt→Re-Encrypt unter dem aktiven Provider. Liefert `200 OK` (clean) oder `207 Multi-Status` (skipped rows mit Details) zurück. |

### Wichtige Dateien

- [src/NodePilot.Core/Interfaces/ISecretProtector.cs](../src/NodePilot.Core/Interfaces/ISecretProtector.cs)
- [src/NodePilot.Data/Security/DpapiSecretProtector.cs](../src/NodePilot.Data/Security/DpapiSecretProtector.cs)
- [src/NodePilot.Data/Security/AesGcmSecretProtector.cs](../src/NodePilot.Data/Security/AesGcmSecretProtector.cs)
- [src/NodePilot.Data/Security/MigratingSecretProtector.cs](../src/NodePilot.Data/Security/MigratingSecretProtector.cs)
- [src/NodePilot.Data/Security/SecretProtectorRegistry.cs](../src/NodePilot.Data/Security/SecretProtectorRegistry.cs)
- [src/NodePilot.Api/Controllers/SecretsController.cs](../src/NodePilot.Api/Controllers/SecretsController.cs)
- [src/NodePilot.Data/CredentialStore.cs](../src/NodePilot.Data/CredentialStore.cs) (`ReencryptAllCredentialsAsync`)
- [src/NodePilot.Data/GlobalVariableStore.cs](../src/NodePilot.Data/GlobalVariableStore.cs) (`ReencryptAllSecretsAsync`)
- [docs/secrets-providers.md](secrets-providers.md) — Operator-Doku mit Migrations-Runbook

### Bewusst nicht in Scope

- **HashiCorp Vault Transit / Azure Key Vault / KMIP** — V2. Heute liegt der AES-Key auf
  der Maschine (Env-Var/RegistryKey); ein echter Vault-Roundtrip pro `Unprotect` wäre
  teuer und führt eine zweite Verfügbarkeitsabhängigkeit ein. Die `ISecretProtector`-
  Schnittstelle ist so entworfen, dass eine Network-Backed-Implementierung in einer Klasse
  + DI-Zeile nachgereicht werden kann.
- **HSM-Backed Keys** — der AES-Provider arbeitet mit Software-Bytes. PKCS#11- oder
  Windows-CNG-backed-Keys sind V2.
- **Per-Row Key-ID / Multi-Key-Decrypt** — der 1-Byte-Version-Header im AES-GCM-Envelope
  ist der Hook dafür; aktuell wird nur `0x01` akzeptiert. Bis dahin geht Key-Rotation über
  den `LegacyMasterKey`-Migrations-Pfad.
- **Automatischer Background-Sweep** — der Re-Encrypt-Sweep ist explizit admin-getriggert,
  damit der Audit-Trail klar zeigt wann jemand rotiert hat.

### Field-Test

```powershell
# 1. Mit DPAPI booten, Credential anlegen
dotnet run --project src/NodePilot.Api --urls http://localhost:5000
# Über UI eine Credential "test-cred" mit Passwort "secret123" erstellen.

# 2. Stoppen, Provider-Rotation konfigurieren — AesGcm aktiv, Dpapi als Legacy-Fallback.
$env:Secrets__Provider='AesGcm'
$env:Secrets__MasterKey='<base64-32-bytes>'
$env:Secrets__LegacyProvider='Dpapi'
$env:Secrets__LegacyDpapiScope='LocalMachine'

# 3. Booten — Boot-Log zeigt:
#    "[Secrets] Migrating secret protector enabled: active=AesGcm, legacy=Dpapi.
#     Run POST /api/secrets/reencrypt then remove Secrets:LegacyProvider once the
#     legacy_reads counter is zero."

# 4. Bulk-Re-Encrypt triggern.
$body = @{username='admin'; password='admin123'} | ConvertTo-Json
$login = Invoke-RestMethod -Uri http://localhost:5000/api/auth/login -Method POST -Body $body -ContentType 'application/json'
$headers = @{ Authorization = "Bearer $($login.token)" }
Invoke-RestMethod -Uri http://localhost:5000/api/secrets/reencrypt -Method POST -Headers $headers
#  → 200 OK + { credentialsRewritten: 1, ..., partialSuccess: false }

# 5. Stoppen, Legacy-Config entfernen, neu booten — Provider ist jetzt rein AES-GCM.
Remove-Item Env:Secrets__LegacyProvider, Env:Secrets__LegacyDpapiScope
```

Verifikation: `SELECT EncryptedPassword FROM Credentials WHERE Name='test-cred'` zeigt
nach dem Sweep einen Wert, der mit dem AES-GCM-Header (`0x01`) beginnt — DPAPI-Werte
starten anders.

---

## 3. SIEM-Logging (ECS-JSON)

### Was es kann

- Schreibt jeden Application-Log-Event als **eine Zeile JSON** im Elastic Common Schema 1.x
  in das Rolling-Logfile. Filebeat/Vector/Fluentd liest das ohne Parser-Konfiguration und
  liefert Elastic / Splunk HEC / Microsoft Sentinel / Datadog identisch.
- **Audit-Events mit voller ECS-Feld-Abdeckung**: jeder erfolgreiche
  `IAuditWriter.LogAsync`-Call emittiert eine Serilog-INFO-Zeile mit den strukturierten
  Properties `event.action`, `event.category` (gemappt aus dem Action-Verb: Login/
  Credential/Permission → `iam`, Execution → `process`, Rest → `configuration`),
  `event.kind=event`, `event.outcome=success`, `event.dataset=nodepilot.audit`, `event.id`
  (AuditLog-Row-ID) und `event.original` (redaktierte Details-JSON), plus `user.id` /
  `user.name` / `source.ip`. Out-of-the-box Sigma-/Sentinel-/Elastic-Detection-Rules
  matchen damit ohne Custom-Mapping.
- **ECS-Root-Felder** in zwei Kategorien werden an die JSON-Wurzel gehoben:
  - **Host/Service-Identity**: `service.*`, `host.*`, `deployment.*`, `agent.*`, `cloud.*`,
    `container.*`.
  - **Per-Event-Felder**: `event.*`, `user.*`, `source.*`, `trace.*`, `span.*`, `error.*`,
    `client.*`, `network.*`, `url.*`, `http.*`.
- **Domain-Properties** (Workflow-/Execution-/Step-IDs etc.) ohne ECS-Prefix landen unter
  `nodepilot.*` mit `snake_case`-Naming.
- **Duplicate-Key-Dedup**: wenn zwei Source-Property-Namen auf denselben Snake-Case-
  Target normalisieren (`WorkflowId` und `workflow_id` beide → `workflow_id`), gewinnt
  der zuletzt geschriebene. Pinning verhindert dass strikte Ingest-Pipelines
  (Filebeat-strict, Splunk-HEC-validating) auf Duplikate werfen.

### Wie es umgesetzt ist

- **`EcsJsonFormatter`** (`src/NodePilot.Api/Logging/`) implementiert
  `Serilog.Formatting.ITextFormatter`. Ein `Utf8JsonWriter`-Pass pro Event:
  - Reserved Felder hart geschrieben: `@timestamp`, `log.level`, `message`, `ecs.version`.
  - `Exception` → strukturiertes `error: { type, message, stack_trace }`-Objekt.
  - Properties werden gebucketed: ECS-Prefix-Match → JSON-Root-Subobjekt, sonst →
    `nodepilot.*`. Innerhalb jedes Buckets dedupliziert `DedupByNormalizedName` per
    last-wins, damit Duplicate-Key-Inputs keine Doppel-Schreibungen produzieren.
  - PascalCase → snake_case-Konversion bei der Property-Namen-Übersetzung.
- **`LoggingSetup`** im `Program.cs` liest `Logging:Format` und schaltet zwischen
  `text` (Default), `cmtrace`, `json` (CLEF), `ecs-json`.
- **`AuditWriter.LogAsync`** wickelt den SIEM-Forward in `Begin…1228 tokens truncated…er-Request-Cache mit `(userId, folderId)` als Key — Service ist scoped, lebt nur
    für den Request, kein Cache-Invalidation-Problem zwischen Usern.
  - **Cache-Invalidate** nach jedem Folder/Permission-Mutation, damit eine Capability-
    Computation in derselben Response die gerade applied changes reflektiert.
  - **Globaler Role-Cap** in `CanAccessWorkflowAsync`/`CanAccessFolderAsync` UND
    `GetWorkflowCapabilitiesAsync` — UI und API agreen auf das gleiche Ergebnis.
- **`WorkflowsControllerBase.RequireWorkflowAccessAsync`** ist der zentrale Helper für
  jeden Workflow-Endpoint: erst Read prüfen (404 bei Fail = existence hide), dann die
  konkrete Operation (403 bei Fail = sichtbar aber nicht erlaubt).
- **`SharedWorkflowFoldersController` + `SharedFolderPermissionsController`** liefern die
  CRUD-Surface: Create/Rename/Move/Delete von Foldern, Grant/Update/Revoke von
  Permissions. Beide gated durch `_authz.CanAccessFolderAsync(... ResourceOp.Admin)`.
- **Permission-Backfill (bestehende User)**: einmaliges `INSERT … SELECT` aus
  `Users` → `SharedFolderPermissions` (Operator → FolderEditor auf Root, Viewer →
  FolderViewer auf Root). Dieses Backfill und der Root-Sentinel-Seed (`HasData`) sind in
  die `InitialBaseline`-Migration (`20260511183144`) zusammengefasst — auf einer frischen
  DB greift es einmalig beim ersten `Migrate()`. Ein früherer Runtime-Reseed-Loop im
  `MigrationBootstrapper` (der bei jedem Boot Admin-Revokes wieder eingespielt hätte) ist
  weg; der Seed läuft genau einmal.
- **`UsersController.Create`** legt für neue Operator/Viewer beim Anlegen eine Default-
  Permission auf Root mit, damit nach dem Migrationsschritt keine zweite Code-Zeile mehr
  den Default verteilen muss.

### Default-Mapping (Migration + Create)

| Globale UserRole | Folder-Permission auf Root |
|---|---|
| Admin | keine (globaler Bypass) |
| Operator | FolderEditor |
| Viewer | FolderViewer |

### Konfiguration

Keine — RBAC ist immer aktiv. Der Default lässt das System unverändert wirken (jeder
Operator/Viewer hat dieselben Rechte wie vor der Migration auf Root + Subtree).
Folder + Grants ändert ein globaler Admin via UI oder direkt am API.

### Wichtige Dateien

- [src/NodePilot.Core/Models/SharedWorkflowFolder.cs](../src/NodePilot.Core/Models/SharedWorkflowFolder.cs)
- [src/NodePilot.Core/Models/SharedFolderPermission.cs](../src/NodePilot.Core/Models/SharedFolderPermission.cs)
- [src/NodePilot.Core/Interfaces/IResourceAuthorizationService.cs](../src/NodePilot.Core/Interfaces/IResourceAuthorizationService.cs)
- [src/NodePilot.Api/Security/ResourceAuthorizationService.cs](../src/NodePilot.Api/Security/ResourceAuthorizationService.cs)
- [src/NodePilot.Api/Controllers/SharedWorkflowFoldersController.cs](../src/NodePilot.Api/Controllers/SharedWorkflowFoldersController.cs)
- [src/NodePilot.Api/Controllers/SharedFolderPermissionsController.cs](../src/NodePilot.Api/Controllers/SharedFolderPermissionsController.cs)
- [src/NodePilot.Api/Controllers/WorkflowsControllerBase.cs](../src/NodePilot.Api/Controllers/WorkflowsControllerBase.cs)
- [src/NodePilot.Data/Migrations/20260511183144_InitialBaseline.cs](../src/NodePilot.Data/Migrations/20260511183144_InitialBaseline.cs) — Folder-Schema, Root-Sentinel-Seed (`HasData`) und das Permission-Backfill sind in die Baseline-Migration gesquasht (die früheren `AddSharedWorkflowFolders`/`BackfillSharedFolderUserPermissions`-Migrationen existieren nicht mehr separat)
- Frontend: [src/nodepilot-ui/src/components/workflows/SharedFolderTree.tsx](../src/nodepilot-ui/src/components/workflows/SharedFolderTree.tsx),
  [SharedFolderPermissionsModal.tsx](../src/nodepilot-ui/src/components/workflows/SharedFolderPermissionsModal.tsx),
  [pages/WorkflowsPage.tsx](../src/nodepilot-ui/src/pages/WorkflowsPage.tsx)

### API-Surface (RBAC-spezifisch)

| Endpoint | Auth | Zweck |
|---|---|---|
| `GET /api/shared-workflow-folders` | Authenticated | Folder-Tree (gefiltert auf lesbare Folder + Capabilities pro Row) |
| `POST /api/shared-workflow-folders` | FolderEditor auf Parent | Neuer Sub-Folder |
| `PUT /api/shared-workflow-folders/{id}` | FolderEditor | Rename |
| `POST /api/shared-workflow-folders/{id}/move` | FolderEditor auf Source + Target | Move |
| `DELETE /api/shared-workflow-folders/{id}` | FolderEditor (nur leere Folder) | Delete |
| `POST /api/workflows/{id}/move-folder` | FolderEditor auf Source + Target | Workflow umsortieren |
| `GET /api/shared-workflow-folders/{id}/permissions` | FolderAdmin | Grants listen |
| `POST /api/shared-workflow-folders/{id}/permissions` | FolderAdmin | Grant vergeben |
| `PUT /api/shared-workflow-folders/{id}/permissions/{permId}` | FolderAdmin | Grant ändern |
| `DELETE /api/shared-workflow-folders/{id}/permissions/{permId}` | FolderAdmin | Grant entziehen |

`POST /api/workflows` akzeptiert `FolderId` (optional, Default Root); Server prüft Edit
auf den Zielordner und lehnt sonst mit 403 ab.

### Bewusst nicht in Scope (V1)

- **Role/Group-Principals** — Schema-Support drin (`PrincipalType`-Enum), API + UI
  exposed nur `User`. Group-Mapping kommt mit OIDC: `PrincipalType=Group`,
  `PrincipalId=<group-id-aus-IdP>`.
- **Per-Workflow-Permissions** — V1 vergibt nur auf Folder-Ebene. Wer einen Workflow
  isoliert schützen will, legt einen Sub-Folder an. Eine separate Workflow-ACL würde die
  Resolution-Komplexität verdoppeln.
- **Permission-Templates** — kein „Operator-Template auf 200 Folder kopieren". UI macht
  Bulk-Grants per User.
- **Audit-Filter pro Folder** — `GET /api/audit` ist heute Admin-only und liefert global.
  Per-Folder-Audit-Sicht ist V2 (würde RBAC durch den Audit-Layer brauchen).

### Field-Test

```powershell
# 1. Branch checkout, neu booten — Migration applied automatisch
git checkout feat/rbac-shared-folders
dotnet run --project src/NodePilot.Api

# 2. Als Admin einloggen, neuen Folder + zweiten User anlegen
#    Login: POST /api/auth/login mit admin/admin123
#    Folder: POST /api/shared-workflow-folders {"parentFolderId":null,"name":"Finance"}
#    User:   POST /api/users {"username":"alice","password":"...","role":"Operator"}

# 3. Alice grants auf /Finance: POST /api/shared-workflow-folders/<finance-id>/permissions
#    Body: {"principalType":"User","principalId":"<alice-uuid>","role":"FolderEditor"}

# 4. Als Alice einloggen — sieht Workflows in Root NICHT (nur FolderEditor auf /Finance),
#    sieht /Finance + /Finance/Reports + alle dortigen Workflows.
#    Run/Edit nur in /Finance-Subtree erlaubt; Root-Workflow → 404.
```

---

## 5. Enterprise-Identität und SSO

### Status: AD SSO Preview

Die Enterprise-SSO-Pfade sind implementiert und automatisiert getestet. „Enterprise-ready“
darf für diesen Teil erst nach einem realen Feldtest verwendet werden, der LDAPS und Kerberos
durch den produktionsgleichen HAProxy-Pfad sowie die Ablehnung von NTLM nachweist.

| Pfad | Mechanismus | Default |
|---|---|---:|
| Local | BCrypt, Modi `Disabled | BreakGlassOnly | Enabled` | `BreakGlassOnly` |
| LDAP | AD Simple Bind ausschließlich über LDAPS | aus |
| Windows | Negotiate/Kerberos, NTLM fail-closed | aus |
| OIDC | Authorization Code + PKCE | aus |
| SCIM | SCIM 2.0 Users/Groups + Discovery | aus |

### Tragende Invarianten

- Externe Benutzer werden über `ExternalIdentity(Authority, Subject)` identifiziert.
  Mutable Usernames oder Display Names sind keine Linking-Keys.
- LDAP und Windows verwenden denselben kanonischen AD-`objectSid` unter
  `urn:nodepilot:identity:active-directory`. Beide Protokolle landen dadurch auf
  derselben NodePilot-User-Row.
- Bestehende Benutzer werden nie automatisch zusammengeführt. Username-Kollisionen und
  mehrdeutige Legacy-Mappings werden kontrolliert abgelehnt und auditiert.
- `AuthSession` ist serverseitig widerrufbar. Refresh rotiert den aktuellen JTI atomar;
  ein gestohlenes Token kann nicht parallel zwei gültige Nachfolger erzeugen.
- JWTs enthalten keine Directory-Gruppen. `DirectoryMembership(UserId, Authority, GroupKey)`
  speichert authority-scoped, serverseitige Membership-Snapshots.
- AD-Sync läuft alle ein bis fünf Minuten. Externe Autorisierung darf nie älter als
  `MaxAuthorizationStalenessMinutes` sein; der konfigurierte Maximalwert ist 15 Minuten.
- Tombstone, Deaktivierung oder Gruppenentzug widerruft Sessions und stoppt auch Schedules,
  Webhooks, External Triggers sowie Pending/Running/Paused Executions spätestens innerhalb
  dieses Fensters.
- Folder-Grants für AD-Gruppen verwenden kanonische SIDs. OIDC-/SCIM-Gruppen leben im
  jeweiligen Issuer-Namespace und können AD-Grants nicht durch Namenskollision treffen.
- Folder-Grants speichern `PrincipalAuthority` und `PrincipalKey` gemeinsam. Die Admin-UI
  verlangt für OIDC/SCIM-Gruppen den exakten HTTPS-Issuer; ein fehlender Authority-Wert ist
  nur als Legacy-Kurzform für die kanonische AD-Authority zulässig.
- SignalR und Worker-Dispatch prüfen denselben Account-, Session- und Freshness-Zustand
  wie normale HTTP-Requests.

### AD-Sicherheitsdefaults

Eine aktivierte AD-Konfiguration verlangt:

- LDAPS mit vollständiger Zertifikatsprüfung — das DC-Zertifikat muss gegen den
  Windows-Zertifikatsspeicher des API-Hosts validieren, einen In-App-Bypass gibt es nicht;
  LDAP-Referrals werden nie verfolgt;
- mindestens einen konfigurierten DC, bei HA besser mehrere `Endpoints`;
- `BaseDn`, für LDAP-Login zusätzlich `UpnSuffix`;
- Service-Bind-DN und Passwort für Sync/Deprovisioning;
- mindestens eine `AllowedGroupSids`-SID;
- `DirectorySyncIntervalMinutes` zwischen 1 und 5;
- für Windows SSO `AllowNtlmFallback=false` und
  `NtlmDisabledByPolicy=true` nach tatsächlich ausgerollter Host-/Domain-Policy.

```jsonc
{
  "Authentication": {
    "LocalLoginMode": "BreakGlassOnly",
    "SessionAbsoluteLifetimeHours": 8,
    "MaxAuthorizationStalenessMinutes": 15,
    "Ldap": {
      "Enabled": false,
      "Endpoints": ["dc01.contoso.example:636", "dc02.contoso.example:636"],
      "Port": 636,
      "UseSsl": true,
      "BaseDn": "DC=contoso,DC=example",
      "UpnSuffix": "contoso.example",
      "ServiceBindDn": "CN=svc-nodepilot,OU=Service Accounts,DC=contoso,DC=example",
      "ServicePassword": "<secret>",
      "AllowedGroupSids": ["S-1-5-21-...-1200"],
      "DirectorySyncIntervalMinutes": 5
    },
    "Windows": {
      "Enabled": false,
      "AllowNtlmFallback": false,
      "NtlmDisabledByPolicy": false
    }
  }
}
```

Das ausgelieferte Template lässt die Provider deaktiviert. Bei Aktivierung muss
`NtlmDisabledByPolicy` erst nach verifizierter Policy auf `true` gesetzt werden.
Authentication-Schemes werden beim Prozessstart registriert; Änderungen erfordern einen
Service-Neustart.

### HAProxy/Kerberos

Negotiate ist connection-scoped. Das ausgelieferte
[HAProxy-Template](../deploy/templates/haproxy.cfg.template) erzwingt daher:

- persistente HTTP/1.1-Frontend- und Backend-Verbindungen;
- `http-reuse never`, damit keine authentifizierte Backend-Verbindung zwischen Clients
  wiederverwendet wird;
- Source-Affinity und Active/Passive-Healthchecks;
- Backend-TLS mit `verify required`, CA, SNI und Hostname-Prüfung;
- Entfernen und vertrauenswürdiges Neuerzeugen von Forwarded Headers.

Nur die Transport-IP des Proxys gehört in `ForwardedHeaders:KnownProxies`. SPN,
Browser-Intranet-Policy und NTLM-Block-Policy bleiben explizite Deployment-Aufgaben.

### LDAP-Failover und Offboarding

Der Directory-Lookup befragt alle konfigurierten DCs. „User nicht gefunden“ wird nur
akzeptiert, wenn jeder konfigurierte DC dies bestätigt. Gefundene Snapshots gelten nur
dann als frisch, wenn alle DCs erreichbar sind und bei Aktivitätsstatus sowie Gruppen
übereinstimmen. Ein Mix aus Found/Not-Found, Enabled/Disabled, abweichende Gruppen oder
ein nicht erreichbarer DC werden als uneindeutiger Sync-Fehler behandelt und aktualisieren
`LastDirectorySyncAt` nicht. Nach Ablauf des letzten gültigen Snapshots bleibt die
Autorisierung damit fail-closed.

Ein kompletter Pass, in dem alle bekannten AD-Identitäten fehlen, wird als falsche `BaseDn`
oder unzureichende Search-Berechtigung verworfen und erzeugt keine Massen-Tombstones. Der
Directory-Healthcheck prüft alle DCs und meldet den Verlust der Failover-Kapazität als
`Degraded`. Sobald ein externer Provider aktiv ist, verweigert eine bestehende Datenbank
ohne aktiven lokalen Break-Glass-Admin den Start.

Automatisierte Ausführungen tragen den Publisher als effektiven Principal. Vor Worker-Start
werden Aktivität, Tombstone, externe Freshness und aktuelle Folder-Run-Berechtigung erneut
geprüft. Ein Sync mit Autorisierungsverlust widerruft Sessions und beendet betroffene
Ausführungen.

---

## 6. OIDC und SCIM

OIDC validiert Authorization Code, PKCE, State, Nonce, Issuer, Audience und Signatur. Das
temporäre externe Ticket liegt Data-Protection-geschützt serverseitig in
`OidcLoginTickets`; der Browser erhält nur einen opaken Handle. Damit bleibt der
Session-Cookie auch bei 500 IdP-Gruppen unter dem Browser-/Proxy-Limit.

OIDC-Issuer, Subject und Gruppen-IDs werden als opake, case-sensitive Werte behandelt.
Surrounding Whitespace wird abgelehnt. Fehlen Gruppenclaims, ist ein Fallback nur bei einem
expliziten Group-Overage-Signal und einem höchstens 15 Minuten alten, issuer-passenden
SCIM/Membership-Snapshot erlaubt. Ein SCIM-User-Update frischt Gruppenautorität nicht auf.

SCIM 2.0 bietet ServiceProviderConfig-, ResourceTypes- und Schemas-Discovery sowie
Users/Groups unter `/api/scim/v2`. Mutationen sind
serialisierbar transaktional, schützen den letzten aktiven Admin, auditieren Änderungen und
widerrufen betroffene Sessions und Executions. SCIM-Bearer-Tokens müssen 32–4096 Zeichen
lang sein. Für eine überlappende Rotation kann der alte Token vorübergehend als
`Scim:PreviousBearerToken` weiter akzeptiert und anschließend explizit gelöscht werden.
Für gemeinsame Identitäten muss `Scim:Authority` exakt dem OIDC-Issuer
entsprechen.

OIDC und SCIM haben ein separates Enterprise-Release-Gate. Vor Freigabe müssen der konkrete
IdP, Group-Overage, Gruppenentzug, Deprovisioning und Full-Reprovision nach Restore getestet
werden. **SAML bleibt außerhalb des Zielbilds.**

---

## Roll-out und Abnahme

1. Sicheren lokalen Break-Glass-Admin markieren und Recovery-Prozess testen.
2. LDAPS-Trust, beide DCs, Service-Bind und Gruppen-Allowlist konfigurieren; Verbindungstest
   ausführen und Service neu starten.
3. Directory-Sync und Entzug innerhalb von 15 Minuten nachweisen.
4. HTTP-SPN, Browser-Policy, HAProxy-Härtung und NTLM-Block-Policy ausrollen.
5. LDAP und Windows mit derselben realen Person testen und gleiche NodePilot-User-ID prüfen.
6. OIDC/SCIM je IdP separat aktivieren und Provisioning-/Overage-Matrix testen.
7. Erst nach bestandenem realem AD-/Kerberos-/LDAPS-/NTLM-Feldtest den Preview-Status ändern.

Die vollständige Operator-Anleitung und Testmatrix stehen in
[ldap-windows-sso.md](ldap-windows-sso.md).
