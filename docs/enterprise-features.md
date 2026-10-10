# NodePilot Enterprise Features

NodePilot bundles HA, secret providers, SIEM, folder RBAC and enterprise authentication.
The external authentication and provisioning paths are opt-in. The SSO status explicitly stays
**AD SSO Preview** until a real AD/Kerberos/LDAPS field test has passed.

| Feature | Status | Default | Config switch |
|---|---|---|---|
| HA Active/Passive | implemented + field test | Single-node (off) | `Cluster:Enabled=true` |
| Vault / Secret provider | implemented + field test | DPAPI | `Secrets:Provider=aesgcm` |
| SIEM logging (ECS-JSON) | implemented | text | `Logging:Format=ecs-json` |
| RBAC stage A (Shared Folders) | implemented | active (all existing users on Root) | — |
| AD SSO | **Preview**, real field test pending | off | `Authentication:Ldap:Enabled` / `Authentication:Windows:Enabled` |
| OIDC + SCIM 2.0 | implemented, separate release gate | off | `Authentication:Oidc:Enabled` / `Authentication:Scim:Enabled` |

The infrastructure features can still be enabled independently. All login paths, however,
deliberately converge on the same identity, session, membership and offboarding model.

---

## 1. High Availability (Active/Passive)

### What it does

- Two (or more) NodePilot instances share **one** database. At any point in time exactly one
  is the **leader**, accepts mutating API calls and runs workflows.
  All others are **followers** and answer mutating endpoints with `503` +
  `Retry-After: 30`.
- **RTO 40–60 seconds** after a crash: the dead leader's lease expires after at most
  30 s (TTL), the standby acquires it (renew loop every 10 s) and the LB notices on its
  next 5 s probe (≈ TTL + renew + probe). On a **planned** stop the leader actively releases
  its lease during shutdown (`ClusterLeaderService.StopAsync`), so the
  standby takes over on the next 10 s tick → ~10 s.
- **Fencing**: a leader that detects its own step-down (renew returned 0 rows)
  immediately cancels all locally running workflow executions, so the new leader can
  adopt the orphan rows without a write race.
- **Recovery sweep**: the new leader marks foreign `Running`/`Paused` executions
  and orphaned `Pending` executions without a dispatch outbox entry as `Cancelled`.
  It takes over durable `Pending` jobs and releases their dispatch leases.
- **LeaseEpoch** as a monotonic fencing token in every acquire. It ends up in the audit, so
  post-mortems can tell "this was leader incarnation 7, then 8".
- **Terminal write fence**: engine completions write via compare-and-set only from
  `Running`/`Paused`. In HA mode the same DB update checks owner, epoch and lease expiry.
  An old leader therefore cannot overwrite an SSO offboarding `Cancelled`.

### How it is implemented

- **`ClusterLeaderService`** (`src/NodePilot.Scheduler/Cluster/`) is both a
  `BackgroundService` (drives the renew loop) and an `IClusterStateProvider` (all other
  components use it to read "am I leader?").
- Lease acquire/renew as an atomic `UPDATE ... WHERE OwnerNodeId = me AND ExpiresAt > now`,
  so two nodes cannot both believe they are leader at the same time.
- **DB clock instead of app clock**: before every lease operation the service reads `SYSUTCDATETIME()`
  (SQL Server) or `(now() AT TIME ZONE 'UTC')` (Postgres), so that two nodes with
  diverging wall clocks do not run into a split brain.
- **`LeaderRequiredMiddleware`** (`src/NodePilot.Api/Security/`) blocks every mutating
  path on a follower with 503. Allowed: `/healthz/*`, `/openapi/*`, read-only endpoints.
  Defense in depth: the load balancer should not route to followers anyway.
  **Endpoint metadata beats the path heuristic:** the middleware first checks for a
  `[LeaderOnly]` (`Security/LeaderOnlyAttribute.cs`) on the endpoint and only then the
  method/path rules. This is needed for every endpoint whose HTTP verb looks harmless but
  which actually changes state. A `GET` webhook ingress is exactly this case
  (`WebhooksController` carries the attribute). New semantically mutating GETs get it too.
- **`ClusterFailoverRecoveryHost`** subscribes to `OnLeadershipAcquired` in the **constructor**
  (not in `StartAsync`, so the first acquire event does not fire into an empty handler list)
  and calls `StartupRecovery.RecoverOrphanedExecutionsAsync`.
- **`ClusterFencingHost`** subscribes to `OnLeadershipLost` and triggers
  `WorkflowEngine.CancelAllLocalAsync()`. This is a **static** method because
  `_runningExecutions` is process-static. The singleton host does not need a scoped engine.
- **`ClusterLeader` table** with the single-row sentinel `Resource='primary'`. Seeded in
  `MigrationBootstrapper` (runtime, after `Migrate()`: `SeedClusterLeaderRow`, **not** as a
  migration `HasData`). A boot race of two nodes on the insert is caught with `try/catch DbUpdateException`
  + re-query. After the catch it checks whether the row now exists. If yes,
  it is a benign race (log quietly). If no, it is a real DB/permission/schema error (rethrow,
  boot fails loudly). This prevents permission errors from being swallowed as a "race".

### Configuration

```jsonc
{
  "Cluster": {
    "Enabled": false,                  // true = cluster mode
    "NodeId": null,                    // Default: Environment.MachineName
    "LeaseTtlSeconds": 30,             // Lease expires after n s without renew
    "LeaseRenewSeconds": 10,           // Leader renews every n s
    "LeaseDbTimeoutSeconds": 3         // SqlCommand.CommandTimeout for renew
  }
}
```

**Sizing rule of thumb:** RTO ≈ TTL + renew interval + recovery sweep duration.
TTL=30s + Renew=10s + Sweep=~5s → ~45s worst case.

### Key files

- [src/NodePilot.Scheduler/Cluster/ClusterLeaderService.cs](../src/NodePilot.Scheduler/Cluster/ClusterLeaderService.cs)
- [src/NodePilot.Engine/Cluster/SingleNodeClusterStateProvider.cs](../src/NodePilot.Engine/Cluster/SingleNodeClusterStateProvider.cs)
- [src/NodePilot.Api/Hosting/ClusterSetup.cs](../src/NodePilot.Api/Hosting/ClusterSetup.cs)
- [src/NodePilot.Api/Hosting/ClusterFailoverRecoveryHost.cs](../src/NodePilot.Api/Hosting/ClusterFailoverRecoveryHost.cs)
- [src/NodePilot.Api/Hosting/ClusterFencingHost.cs](../src/NodePilot.Api/Hosting/ClusterFencingHost.cs)
- [src/NodePilot.Api/Security/LeaderRequiredMiddleware.cs](../src/NodePilot.Api/Security/LeaderRequiredMiddleware.cs)
- [src/NodePilot.Api/Security/LeaderOnlyAttribute.cs](../src/NodePilot.Api/Security/LeaderOnlyAttribute.cs)
- [src/NodePilot.Engine/Execution/StartupRecovery.cs](../src/NodePilot.Engine/Execution/StartupRecovery.cs)

### Deliberately out of scope

- **Active/Active**: all mutations go through the leader. Active/Active needs
  conflict resolution on every mutating endpoint (workflow lock, execution recovery,
  audit sequence) and is a separate engineering tier.
- **Multi-region**: the lease works against exactly one DB. Cross-region requires
  geo-replication + conflict detection.

### Field test

```powershell
# 1. Start Postgres
& 'C:\NodePilot-Postgres\pgsql\bin\pg_ctl.exe' start -D 'C:\NodePilot-Postgres\data' -w

# 2. Start two instances with Cluster:Enabled=true (different ports)
$env:Cluster__Enabled='true'; $env:Cluster__NodeId='node-a'
dotnet run --project src/NodePilot.Api --urls http://localhost:5000

# In a second terminal
$env:Cluster__Enabled='true'; $env:Cluster__NodeId='node-b'
dotnet run --project src/NodePilot.Api --urls http://localhost:5001

# 3. Find the leader: GET /healthz/leader → 200 for the leader, 503 for followers
curl http://localhost:5000/healthz/leader
curl http://localhost:5001/healthz/leader

# 4. Kill the leader, start a stopwatch, wait until curl against :5001 returns 200 again
```

Expected: 40–60 s until `/healthz/leader` turns green on node-b. The audit log shows
`LeaseEpoch` increasing monotonically (1 → 2).

---

## 2. Vault / Pluggable Secret Provider

### What it does

- Encrypts **credentials**, **global variables** and complete historical
  **workflow version definitions** at rest. Previously hard-wired to Windows
  DPAPI. The feature introduces a provider abstraction and ships a
  second implementation based on AES-GCM with the key from an environment variable.
- **Provider migration** via a `MigratingSecretProtector` wrapper: for the duration
  of the rotation a second (legacy) provider runs in parallel. Reads try active
  first and fall back to legacy. Writes always use active. An admin-triggered
  bulk sweep (`POST /api/secrets/reencrypt`) runs credentials, secret globals, workflow versions, agent MCP secrets, notification routes, pending
  dispatch parameters and runtime settings files through decrypt→encrypt and
  ends the migration window. Skipped rows (e.g. corrupt ciphertexts) are listed by name
  in the response. HTTP `207 Multi-Status` signals "not everything
  migrated", `200 OK` only on a clean cutover.
- **HA guardrail**: `Cluster:Enabled=true` + `Secrets:Provider=Dpapi` (or empty default)
  crashes at boot. DPAPI is machine-bound, so the standby could never decrypt
  what the leader writes. Hard fail instead of a silently broken cluster.
- **Provider typo hard fail**: unknown `Secrets:Provider` values (e.g. `AesGCMm`) are
  rejected at boot. There is no silent fallback to DPAPI anymore.
- **Fail-loud globals**: if a workflow references `{{globals.STRIPE_KEY}}` and
  STRIPE_KEY exists in the DB but cannot be decrypted (scope
  mismatch, key rotation without sweep), the workflow fails **before the first step** with
  a clear error message. The literal is not silently substituted into an HTTP header.
- **Audit of crypto operations** via metrics: `nodepilot_credential_crypto_calls{operation,result}`
  distinguishes `encrypt`/`decrypt` × `success`/`failure`. `nodepilot_credential_crypto_legacy_reads`
  counts decrypts served by the legacy provider during the migration window. Even when the
  counter is at zero, the legacy config may only be removed after a clean sweep with
  `workflowVersionsSkipped=0`.

### How it is implemented

- **`ISecretProtector`** (`src/NodePilot.Core/Interfaces/`): minimal interface
  `Protect(byte[]) → byte[]`, `Unprotect(byte[]) → byte[]`, `Name`. Stateless, thread-safe.
- **`DpapiSecretProtector`**: the default. Reads `Credentials:DpapiScope` (`CurrentUser` |
  `LocalMachine`). `LocalMachine` is the production recommendation because it survives
  a change of service account.
- **`AesGcmSecretProtector`**: the 32-byte key is read Base64-encoded from the config key
  `Secrets:MasterKey` (typically via the env var `Secrets__MasterKey`, not in
  `appsettings.json`). 96-bit random nonce per encryption, 128-bit tag
  for integrity. A format header marks `nodepilot-aesgcm-v1` so that future
  algorithm changes work without a DB sweep.
- **`SecretProtectorRegistry`** chooses at boot based on `Secrets:Provider`:
  - Without `Secrets:LegacyProvider` → exactly one implementation is registered in DI as
    `ISecretProtector`.
  - With `Secrets:LegacyProvider` → the active one is wrapped in `MigratingSecretProtector`,
    which tries reads first via active, then via legacy. Writes always go via active.
  - DPAPI scope values are strictly validated via `DpapiScopeResolver.Parse`, both for
    `Credentials:DpapiScope` and for `Secrets:LegacyDpapiScope`. Typos such as
    `Local_Machine` crash instead of silently falling back to `CurrentUser`.
- **`MigratingSecretProtector`** is a thin decorator. On a decrypt failure under the
  active implementation the legacy implementation takes over. If the plaintext stays empty,
  a combined `CryptographicException` diagnostic is thrown that names both
  attempts.
- **`POST /api/secrets/reencrypt`** (admin-only) reads every credential, secret global
  variable, encrypted `WorkflowVersion.DefinitionJson`, agent MCP secret, notification route,
  pending dispatch parameter and runtime settings file (rollback files included), decrypts it via
  the (possibly wrapping) protector, re-encrypts it under the active provider and writes it
  back. Each of the seven families returns its own rewritten/skipped counters and
  `(id, name, reason)` details. `LegacyProvider` stays set as long as, in particular, a
  history skip is outstanding.
- **DI disambiguation via `[ActivatorUtilitiesConstructor]`**: `CredentialStore` and
  `GlobalVariableStore` have several constructors (legacy + new single-arg path with
  protector). Microsoft.Extensions.DependencyInjection would otherwise throw
  `AmbiguousMatchException`. The attribute explicitly marks the "right"
  constructor.

### Configuration

```jsonc
{
  "Credentials": {
    "DpapiScope": "LocalMachine"            // CurrentUser | LocalMachine (DPAPI path)
  },
  "Secrets": {
    "Provider": "Dpapi",                    // "Dpapi" (default) | "AesGcm"
    "MasterKey": null,                      // base64-encoded 32 bytes, required for AesGcm

    // Optional, only set during a provider rotation: the old provider is
    // wrapped as a read fallback so the bulk re-encrypt sweep can read old rows.
    "LegacyProvider": null,                 // "Dpapi" | "AesGcm" (or empty)
    "LegacyDpapiScope": null,               // CurrentUser | LocalMachine (for Legacy=Dpapi)
    "LegacyMasterKey": null                 // base64, for Legacy=AesGcm (master key rotation)
  }
}
```

```powershell
# Generate a key (32 random bytes, Base64-encoded)
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

Then store the key value wherever the service account gets it handed over, e.g. the
Windows service `RegistryKey` `HKLM\SYSTEM\CurrentControlSet\Services\NodePilot\Environment`
with the MULTI_SZ value `Secrets__MasterKey=<base64>` (maps to the config key
`Secrets:MasterKey`). **Never commit it to the repo**, never write it to the
log file. `SecretProtectorRegistry` starts with a hardening warning if the
key is in plain text in `appsettings.json`.

### API surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/secrets/reencrypt` | Admin | Bulk sweep of all credentials, secret globals, workflow versions, agent MCP secrets, notification routes, pending dispatch parameters and runtime settings files under the active provider. Returns `200 OK` (clean) or `207 Multi-Status` (separate skip details per family). |

### Key files

- [src/NodePilot.Core/Interfaces/ISecretProtector.cs](../src/NodePilot.Core/Interfaces/ISecretProtector.cs)
- [src/NodePilot.Data/Security/DpapiSecretProtector.cs](../src/NodePilot.Data/Security/DpapiSecretProtector.cs)
- [src/NodePilot.Data/Security/AesGcmSecretProtector.cs](../src/NodePilot.Data/Security/AesGcmSecretProtector.cs)
- [src/NodePilot.Data/Security/MigratingSecretProtector.cs](../src/NodePilot.Data/Security/MigratingSecretProtector.cs)
- [src/NodePilot.Data/Security/SecretProtectorRegistry.cs](../src/NodePilot.Data/Security/SecretProtectorRegistry.cs)
- [src/NodePilot.Api/Controllers/SecretsController.cs](../src/NodePilot.Api/Controllers/SecretsController.cs)
- [src/NodePilot.Data/CredentialStore.cs](../src/NodePilot.Data/CredentialStore.cs) (`ReencryptAllCredentialsAsync`)
- [src/NodePilot.Data/GlobalVariableStore.cs](../src/NodePilot.Data/GlobalVariableStore.cs) (`ReencryptAllSecretsAsync`)
- [src/NodePilot.Api/Services/WorkflowVersionDefinitionProtector.cs](../src/NodePilot.Api/Services/WorkflowVersionDefinitionProtector.cs) (`ReencryptAllAsync`)
- [docs/secrets-providers.md](secrets-providers.md): operator documentation with the migration runbook

### Deliberately out of scope

- **HashiCorp Vault Transit / Azure Key Vault / KMIP**: V2. Today the AES key lives on
  the machine (env var/RegistryKey). A real vault round trip per `Unprotect` would be
  expensive and introduce a second availability dependency. The `ISecretProtector`
  interface is designed so that a network-backed implementation can be added later as one class
  + one DI line.
- **HSM-backed keys**: the AES provider works with software bytes. PKCS#11 or
  Windows CNG-backed keys are V2.
- **Per-row key ID / multi-key decrypt**: the 1-byte version header in the AES-GCM envelope
  is the hook for this. Currently only `0x01` is accepted. Until then, key rotation goes through
  the `LegacyMasterKey` migration path.
- **Automatic background sweep**: the re-encrypt sweep is explicitly admin-triggered,
  so the audit trail clearly shows when someone rotated.

### Field test

```powershell
# 1. Boot with DPAPI, create a credential
dotnet run --project src/NodePilot.Api --urls http://localhost:5000
# Create a credential "test-cred" with password "secret123" in the UI.

# 2. Stop, configure provider rotation: AesGcm active, Dpapi as legacy fallback.
$env:Secrets__Provider='AesGcm'
$env:Secrets__MasterKey='<base64-32-bytes>'
$env:Secrets__LegacyProvider='Dpapi'
$env:Secrets__LegacyDpapiScope='LocalMachine'

# 3. Boot. The boot log shows:
#    "Secret protector enabled. Provider: AesGcm+Dpapi-fallback. Run POST
#     /api/secrets/reencrypt and resolve every skip before removing
#     Secrets:LegacyProvider."

# 4. Trigger the bulk re-encrypt.
$body = @{username='admin'; password='admin123'} | ConvertTo-Json
$login = Invoke-RestMethod -Uri http://localhost:5000/api/auth/login -Method POST -Body $body -ContentType 'application/json'
$headers = @{ Authorization = "Bearer $($login.token)" }
Invoke-RestMethod -Uri http://localhost:5000/api/secrets/reencrypt -Method POST -Headers $headers
#  → Remove the legacy config only on 200 OK + partialSuccess:false (all seven skip counters zero)

# 5. Only after a clean sweep (all seven skip counters zero, on every node): stop, remove the legacy config
#    and boot again. The provider is now pure AES-GCM.
Remove-Item Env:Secrets__LegacyProvider, Env:Secrets__LegacyDpapiScope
```

Verification: after the sweep, `SELECT EncryptedPassword FROM Credentials WHERE Name='test-cred'` shows
a value that starts with the AES-GCM header (`0x01`). DPAPI values
start differently.

---

## 3. SIEM Logging (ECS-JSON)

### What it does

- Writes every application log event as **one line of JSON** in Elastic Common Schema 1.x
  to the rolling log file. Filebeat/Vector/Fluentd read it without parser configuration and
  deliver it identically to Elastic / Splunk HEC / Microsoft Sentinel / Datadog.
- **Audit events with full ECS field coverage**: every successful
  `IAuditWriter.LogAsync` call emits a Serilog INFO line with the structured
  properties `event.action`, `event.category` (mapped from the action verb: Login/
  Credential/Permission → `iam`, Execution → `process`, rest → `configuration`),
  `event.kind=event`, `event.outcome=success`, `event.dataset=nodepilot.audit`, `event.id`
  (AuditLog row ID) and `event.original` (redacted details JSON), plus `user.id` /
  `user.name` / `source.ip`. Out-of-the-box Sigma/Sentinel/Elastic detection rules
  therefore match without custom mapping.
- **ECS root fields** in two categories are lifted to the JSON root:
  - **Host/service identity**: `service.*`, `host.*`, `deployment.*`, `agent.*`, `cloud.*`,
    `container.*`.
  - **Per-event fields**: `event.*`, `user.*`, `source.*`, `trace.*`, `span.*`, `error.*`,
    `client.*`, `network.*`, `url.*`, `http.*`.
- **Domain properties** (workflow/execution/step IDs etc.) without an ECS prefix end up under
  `nodepilot.*` with `snake_case` naming.
- **Duplicate key dedup**: if two source property names normalize to the same snake_case
  target (`WorkflowId` and `workflow_id` both → `workflow_id`), the last one
  written wins. Pinning this prevents strict ingest pipelines
  (Filebeat strict, Splunk HEC validating) from failing on duplicates.

### How it is implemented

- **`EcsJsonFormatter`** (`src/NodePilot.Api/Logging/`) implements
  `Serilog.Formatting.ITextFormatter`. One `Utf8JsonWriter` pass per event:
  - Reserved fields written explicitly: `@timestamp`, `log.level`, `message`, `ecs.version`.
  - `Exception` → structured `error: { type, message, stack_trace }` object.
  - Properties are bucketed: ECS prefix match → JSON root sub-object, otherwise →
    `nodepilot.*`. Within each bucket `DedupByNormalizedName` deduplicates with
    last-wins, so duplicate key inputs do not produce double writes.
  - PascalCase → snake_case conversion when translating property names.
- **`LoggingSetup`** in `Program.cs` reads `Logging:Format` and switches between
  `text` (default), `cmtrace`, `json` (CLEF), `ecs-json`.
- **`AuditWriter.LogAsync`** wraps the SIEM forward in `BeginScope` and, **after**
  `SaveChanges`, emits exactly one `LogInformation` line per audit row. The scope dictionary
  carries the ECS names (`event.action`, `event.category`, `event.kind`, `event.outcome`,
  `event.dataset`, `event.id`, `event.original`, `user.id`, `user.name`, `source.ip`) plus
  the support log fields (`support.event_type`, `support.message`, `SupportLog`).
  - **Support log mirror** applies to an action on the allowlist (auth, user management,
    publish, trigger, secrets) **or** to any `outcome == "failure"`. Brute force,
    failed decryptions and `_FAILED`/`_SUPPRESSED`/`_REJECTED` actions therefore end up
    in the support log automatically, without the allowlist having to know each of them.
  - An error in the audit write **never** aborts the triggering mutation. The only
    operational signal is the `AuditWrites` metric with `result=failure`.

Config keys, field reference and the forwarder recipes are in
[docs/siem-logging.md](siem-logging.md).

## 4. Folder RBAC (Shared Folders)

### What it does

Folder RBAC limits access to workflows through the shared folder they live in.
The folders form a tree (default maximum depth 5). Grants go to users or to
directory groups. The four folder roles build on each other:

```text
FolderViewer < FolderOperator < FolderEditor < FolderAdmin
```

- **Inheritance downwards:** `FolderEditor` on `/Finance` applies to `/Finance/Reports` and
  deeper, without another grant.
- **Highest role wins:** a grant on a subfolder does **not override** downwards.
  Editor on `/Finance` + Viewer on `/Finance/Reports` results in Editor on both.
- **Global Admin bypasses everything**. Global Operator/Viewer are **capped** by their `UserRole`:
  a global Viewer with FolderAdmin still gets no Run/Edit/Admin.
- **Existence hiding:** unreadable workflows return `404` instead of `403`, so their mere
  existence is not disclosed.
- **Capabilities per row** (`canRead`, `canRun`, `canEdit`, `canAdmin`) in list and
  detail responses. The UI only shows buttons the caller is allowed to use.
- **Sub-workflow authorization at runtime:** if workflow A starts workflow B, the
  engine checks the effective principal's read permission on B's folder.
- **SignalR group routing:** execution events only reach the hub groups of users who
  are allowed to read the workflow.
- **Authority-scoped groups:** `PrincipalType=Group` stores `PrincipalAuthority` plus
  `PrincipalKey`. AD uses the canonical AD authority and a Windows SID, OIDC/SCIM use the
  exact HTTPS issuer and the opaque group ID. Evaluation is done exclusively against
  server-side membership snapshots, never against JWT claims.

A FolderAdmin assigns grants in the UI on the **Workflows** page: right-click the
folder in the tree → **Permissions…** (also works on Root `\`), or select the folder
and use the **Permissions…** button at the bottom of the folder card.

### How it is implemented

- **`ResourceAuthorizationService`** (`src/NodePilot.Api/Security/`) is the only
  resolver for "may this principal perform this operation on this folder": it resolves the
  folder ancestor chain, collects user and group grants and reduces them to the
  effective folder role.
  - Per-request cache keyed by `(userId, folderId)`. The service is scoped and lives only
    for the request, so there is no cache invalidation problem between users.
  - **Cache invalidate** after every folder/permission mutation, so that a capability
    computation in the same response reflects the changes just applied.
  - **Global role cap** in `CanAccessWorkflowAsync`/`CanAccessFolderAsync` AND
    `GetWorkflowCapabilitiesAsync`, so UI and API agree on the same result.
- **`WorkflowsControllerBase.RequireWorkflowAccessAsync`** is the central helper for
  every workflow endpoint: first check read (404 on fail = existence hide), then the
  concrete operation (403 on fail = visible but not allowed).
- **`SharedWorkflowFoldersController` + `SharedFolderPermissionsController`** provide the
  CRUD surface: create/rename/move/delete of folders, grant/update/revoke of
  permissions. Both are gated by `_authz.CanAccessFolderAsync(... ResourceOp.Admin)`.
- **Root sentinel:** `20260915180058_InitialBaseline` creates the root folder once
  on a fresh database. The old development history including user backfills
  was consolidated before the first production use. A restart does
  not restore revoked permissions.
- **`UsersController.Create`** creates a default permission on Root for new
  Operator/Viewer users when they are created.

### Default mapping when creating a user

| Global UserRole | Folder permission on Root |
|---|---|
| Admin | none (global bypass) |
| Operator | FolderEditor |
| Viewer | FolderViewer |

### Configuration

None. RBAC is always active. New Operator/Viewer users receive the default
permissions listed above on Root and its subfolders.
A global Admin changes folders + grants via the UI or directly through the API.

### Key files

- [src/NodePilot.Core/Models/SharedWorkflowFolder.cs](../src/NodePilot.Core/Models/SharedWorkflowFolder.cs)
- [src/NodePilot.Core/Models/SharedFolderPermission.cs](../src/NodePilot.Core/Models/SharedFolderPermission.cs)
- [src/NodePilot.Core/Interfaces/IResourceAuthorizationService.cs](../src/NodePilot.Core/Interfaces/IResourceAuthorizationService.cs)
- [src/NodePilot.Api/Security/ResourceAuthorizationService.cs](../src/NodePilot.Api/Security/ResourceAuthorizationService.cs)
- [src/NodePilot.Api/Controllers/SharedWorkflowFoldersController.cs](../src/NodePilot.Api/Controllers/SharedWorkflowFoldersController.cs)
- [src/NodePilot.Api/Controllers/SharedFolderPermissionsController.cs](../src/NodePilot.Api/Controllers/SharedFolderPermissionsController.cs)
- [src/NodePilot.Api/Controllers/WorkflowsControllerBase.cs](../src/NodePilot.Api/Controllers/WorkflowsControllerBase.cs)
- [InitialBaseline](../src/NodePilot.Data/Migrations/20260915180058_InitialBaseline.cs): current folder schema and root sentinel seed (`HasData`)
- Frontend: [src/nodepilot-ui/src/components/workflows/SharedFolderTree.tsx](../src/nodepilot-ui/src/components/workflows/SharedFolderTree.tsx),
  [SharedFolderPermissionsModal.tsx](../src/nodepilot-ui/src/components/workflows/SharedFolderPermissionsModal.tsx),
  [pages/WorkflowsPage.tsx](../src/nodepilot-ui/src/pages/WorkflowsPage.tsx)

### API surface (RBAC-specific)

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/shared-workflow-folders` | Authenticated | Folder tree (filtered to readable folders + capabilities per row) |
| `POST /api/shared-workflow-folders` | FolderEditor on parent | New subfolder |
| `PUT /api/shared-workflow-folders/{id}` | FolderEditor | Rename |
| `POST /api/shared-workflow-folders/{id}/move` | FolderEditor on source + target | Move |
| `DELETE /api/shared-workflow-folders/{id}` | FolderEditor (empty folders only) | Delete |
| `DELETE /api/shared-workflow-folders/{id}?recursive=true` | Admin (global) | Delete **including content**: subfolders and the workflows in them. 423 if a workflow in the subtree is checked out by someone else |
| `POST /api/workflows/{id}/move-folder` | FolderEditor on source + target | Move a workflow to another folder |
| `GET /api/shared-workflow-folders/{id}/permissions` | FolderAdmin | List grants |
| `POST /api/shared-workflow-folders/{id}/permissions` | FolderAdmin | Assign a grant |
| `PUT /api/shared-workflow-folders/{id}/permissions/{permId}` | FolderAdmin | Change a grant |
| `DELETE /api/shared-workflow-folders/{id}/permissions/{permId}` | FolderAdmin | Revoke a grant |

`POST /api/workflows` accepts `FolderId` (optional, default Root). The server checks Edit
on the target folder and otherwise rejects with 403.

### Deliberately out of scope (V1)

- **Role principals**: `PrincipalType=Role` stays reserved in the enum and is rejected by the
  grant API. `User` and `Group` are available. Groups additionally carry the
  `PrincipalAuthority` (AD authority + SID or OIDC/SCIM issuer + group ID).
- **Per-workflow permissions**: V1 grants only at folder level. Anyone who wants to protect a
  single workflow in isolation creates a subfolder. A separate workflow ACL would double the
  resolution complexity.
- **Permission templates**: no "copy an Operator template to 200 folders". The UI does
  bulk grants per user.
- **Audit filter per folder**: `GET /api/audit` is admin-only today and returns global data.
  A per-folder audit view is V2 (it would need RBAC through the audit layer).

### Field test

```powershell
# 1. Boot again. The migration is applied automatically
dotnet run --project src/NodePilot.Api

# 2. Log in as admin, create a new folder + a second user
#    Login: POST /api/auth/login with admin/admin123
#    Folder: POST /api/shared-workflow-folders {"parentFolderId":null,"name":"Finance"}
#    User:   POST /api/users {"username":"alice","password":"...","role":"Operator"}

# 3. Grant Alice access to /Finance: POST /api/shared-workflow-folders/<finance-id>/permissions
#    Body: {"principalType":"User","principalId":"<alice-uuid>","role":"FolderEditor"}

# 4. Log in as Alice. She does NOT see workflows in Root (only FolderEditor on /Finance),
#    she sees /Finance + /Finance/Reports + all workflows there.
#    Run/Edit only allowed in the /Finance subtree. Root workflow → 404.
```

---

## 5. Enterprise Identity and SSO

### Status: AD SSO Preview

The enterprise SSO paths are implemented and covered by automated tests. "Enterprise-ready"
may only be used for this part after a real field test that proves LDAPS and Kerberos
through the production-equivalent HAProxy path as well as the rejection of NTLM.

| Path | Mechanism | Default |
|---|---|---:|
| Local | BCrypt, modes `Disabled | BreakGlassOnly | Enabled` | `BreakGlassOnly` |
| LDAP | AD simple bind exclusively over LDAPS | off |
| Windows | Negotiate/Kerberos, NTLM fail-closed | off |
| OIDC | Authorization Code + PKCE | off |
| SCIM | SCIM 2.0 Users/Groups + Discovery | off |

### Core invariants

- External users are identified via `ExternalIdentity(Authority, Subject)`.
  Mutable usernames or display names are not linking keys.
- LDAP and Windows use the same canonical AD `objectSid` under
  `urn:nodepilot:identity:active-directory`. Both protocols therefore end up on
  the same NodePilot user row.
- Existing users are never merged automatically. Username collisions and
  ambiguous legacy mappings are rejected in a controlled way and audited.
- `AuthSession` can be revoked server-side. Refresh rotates the current JTI atomically,
  so a stolen token cannot produce two valid successors in parallel.
- JWTs contain no directory groups. `DirectoryMembership(UserId, Authority, GroupKey)`
  stores authority-scoped, server-side membership snapshots.
- AD sync runs every one to five minutes. External authorization must never be older than
  `MaxAuthorizationStalenessMinutes`. The maximum configurable value is 15 minutes.
- Tombstone, deactivation or group removal revokes sessions and also stops schedules,
  webhooks, external triggers and Pending/Running/Paused executions within
  this window at the latest.
- Folder grants for AD groups use canonical SIDs. OIDC/SCIM groups live in
  the respective issuer namespace and cannot hit AD grants through a name collision.
- Folder grants store `PrincipalAuthority` and `PrincipalKey` together. The admin UI
  requires the exact HTTPS issuer for OIDC/SCIM groups. A missing authority value is
  only allowed as a legacy short form for the canonical AD authority.
- SignalR and worker dispatch check the same account, session and freshness state
  as normal HTTP requests.

### AD security defaults

An enabled AD configuration requires:

- LDAPS with full certificate validation. The DC certificate must validate against the
  Windows certificate store of the API host. There is no in-app bypass.
  LDAP referrals are never followed;
- at least one configured DC, for HA preferably several `Endpoints`;
- `BaseDn`, and for LDAP login also `UpnSuffix`;
- service bind DN and password for sync/deprovisioning;
- at least one `AllowedGroupSids` SID;
- `DirectorySyncIntervalMinutes` between 1 and 5;
- for Windows SSO `AllowNtlmFallback=false` and
  `NtlmDisabledByPolicy=true` once the host/domain policy has actually been rolled out.

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

The shipped template leaves the providers disabled. When enabling them,
`NtlmDisabledByPolicy` may only be set to `true` after the policy has been verified.
Authentication schemes are registered at process start. Changes require a
service restart.

### HAProxy/Kerberos

Negotiate is connection-scoped. The shipped
[HAProxy template](../deploy/templates/haproxy.cfg.template) therefore enforces:

- persistent HTTP/1.1 frontend and backend connections;
- `http-reuse never`, so that no authenticated backend connection is reused
  between clients;
- source affinity and active/passive health checks;
- backend TLS with `verify required`, CA, SNI and hostname validation;
- removal and trusted regeneration of forwarded headers.

Only the proxy's transport IP belongs in `ForwardedHeaders:KnownProxies`. SPN,
browser intranet policy and NTLM block policy remain explicit deployment tasks.
Concrete instructions including GPO paths and a script template are in
[`docs/ldap-windows-sso.md`](ldap-windows-sso.md). Two pitfalls from there: an HTTP SPN
on the **computer account** does not cover a service running under a gMSA, and without a
browser allowlist every client prompts for credentials instead of signing in silently via ticket.

### LDAP directory consensus and offboarding

> **No login failover:** the password bind tries the endpoints in order, but the
> authoritative lookup afterwards requires **all-DC consensus**. A single unreachable DC
> makes the external login fail closed (503) instead of falling back to a surviving DC.
> Only configure DCs that are reachable together (one always-on DC is the
> simplest correct topology).

The directory lookup queries all configured DCs. "User not found" is only
accepted if every configured DC confirms it. Found snapshots only count
as fresh if all DCs are reachable and agree on activity status and groups.
A mix of found/not found, enabled/disabled, diverging groups or
an unreachable DC is treated as an ambiguous sync error and does not update
`LastDirectorySyncAt`. After the last valid snapshot expires,
authorization therefore stays fail-closed.

A complete pass in which all known AD identities are missing is discarded as a wrong `BaseDn`
or insufficient search permission and does not create mass tombstones. The
directory health check checks all DCs and reports an unreachable DC as
`Degraded`. This is only a health indicator. External logins already fail closed with a single
unreachable DC (see the consensus note above). As soon as an external provider is active, an existing database
without an active local break-glass admin refuses to start.

Automated executions carry the effective principal from `Workflow.PublishedByUserId`:
the publisher, or for a workflow that was never published, the user who enabled it
(`/enable` only fills the column when it is empty and never overwrites an existing
publisher). Before the worker starts, activity, tombstone, external freshness and current
folder run permission are checked again. A sync with loss of authorization revokes sessions
and ends affected executions.

---

## 6. OIDC and SCIM

OIDC validates authorization code, PKCE, state, nonce, issuer, audience and signature. The
temporary external ticket is stored server-side, protected by Data Protection, in
`OidcLoginTickets`. The browser only receives an opaque handle. This keeps the
session cookie below the browser/proxy limit even with 500 IdP groups.

OIDC issuer, subject and group IDs are treated as opaque, case-sensitive values.
Surrounding whitespace is rejected. If group claims are missing, a fallback is only allowed with an
explicit group overage signal and an issuer-matching SCIM/membership snapshot that is at most
15 minutes old. A SCIM user update does not refresh group authority.

SCIM 2.0 provides ServiceProviderConfig, ResourceTypes and Schemas discovery as well as
Users/Groups under `/api/scim/v2`. Mutations are
serializable transactions, protect the last active admin, audit changes and
revoke affected sessions and executions. SCIM bearer tokens must be 32–4096 characters
long. For an overlapping rotation the old token can temporarily still be accepted as
`Scim:PreviousBearerToken` and then explicitly deleted.
For shared identities `Scim:Authority` must exactly match the OIDC
issuer.

OIDC and SCIM have a separate enterprise release gate. Before release, the concrete
IdP, group overage, group removal, deprovisioning and full reprovision after restore must be
tested. **SAML stays outside the target scope.**

---

## Rollout and acceptance

1. Mark a secure local break-glass admin and test the recovery process.
2. Configure LDAPS trust, both DCs, service bind and group allowlist. Run the connection test
   and restart the service.
3. Prove directory sync and revocation within 15 minutes.
4. Roll out HTTP SPN, browser policy, HAProxy hardening and NTLM block policy.
5. Test LDAP and Windows with the same real person and check that the NodePilot user ID is the same.
6. Enable OIDC/SCIM separately per IdP and test the provisioning/overage matrix.
7. Only change the preview status after a passed real AD/Kerberos/LDAPS/NTLM field test.

The complete operator guide and test matrix are in
[ldap-windows-sso.md](ldap-windows-sso.md).
