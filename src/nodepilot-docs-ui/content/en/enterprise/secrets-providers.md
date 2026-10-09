# Secret providers

NodePilot encrypts stored machine credentials and global variables marked as secret through `ISecretProtector`. The active provider determines whether encrypted values are bound to one machine or can be moved between cluster nodes.

DPAPI and AES-GCM are available today. HashiCorp Vault Transit is planned as a future provider.

## Provider matrix

| Provider | `Secrets:Provider` | Cluster-portable | Setup |
|---|---|---|---|
| **DPAPI** (default) | `Dpapi` (or unset) | ❌ machine-bound | `Credentials:DpapiScope=LocalMachine` for production |
| **AES-GCM** | `AesGcm` | ✅ | A shared `Secrets:MasterKey` or `Secrets:MasterKeyFile` (32-byte base64) on every node |
| HashiCorp Vault Transit | (not yet) | ✅ | Roadmap |

## Startup guardrails

- Unknown `Secrets:Provider` values (a typo such as `AesGCMm`) **fail at boot** — there is no silent fallback to DPAPI.
- `Cluster:Enabled=true` + `Secrets:Provider=Dpapi` (or the default) **fails at boot** — DPAPI ciphertexts are machine-bound, and the standby could not decrypt them after a failover.

## DPAPI

```jsonc
"Secrets": { "Provider": "Dpapi" },
"Credentials": { "DpapiScope": "LocalMachine" }
```

`LocalMachine` is the production recommendation (it survives a service-account change); `CurrentUser` (the development default) rebinds when the account changes.

## AES-GCM

```jsonc
"Secrets": {
  "Provider": "AesGcm",
  "MasterKeyFile": "C:\\ProgramData\\NodePilot\\secrets\\aesgcm-masterkey.txt"
}
```

`Secrets:MasterKey` remains supported for environment-variable deployments (`Secrets__MasterKey`).
For disk-backed deployments, prefer `Secrets:MasterKeyFile` and restrict the file's ACL to the
NodePilot service identity.

Generate the key and copy it to every cluster node:

```powershell
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 32
try {
    $rng.GetBytes($bytes)
    [Convert]::ToBase64String($bytes)
} finally {
    $rng.Dispose()
    [Array]::Clear($bytes, 0, $bytes.Length)
}
# or
openssl rand -base64 32
```

**Wire format:** `[1 byte version=0x01] [12 bytes nonce] [N bytes ciphertext] [16 bytes auth tag]`. The version byte is the hook for future key-rotation envelopes; today only `0x01` exists.

**Hardening:** the master key has to be available to AES-GCM, but it does not have to live in JSON.
Prefer `Secrets__MasterKey` or an ACL-restricted `Secrets:MasterKeyFile`; use a JSON-backed
`Secrets:MasterKey` only with restrictive ACLs on `appsettings.Production.json` and without
unencrypted backups.

## Migrating DPAPI → AES-GCM

**Step 1 — wire up both providers:**

```jsonc
"Secrets": {
  "Provider": "AesGcm",                  // active: writes, and reads try here first
  "MasterKeyFile": "C:\\ProgramData\\NodePilot\\secrets\\aesgcm-masterkey.txt",
  "LegacyProvider": "Dpapi",             // fallback for rows still in the DPAPI format
  "LegacyDpapiScope": "LocalMachine"
}
```

**Step 2 — bulk re-encrypt:**

```bash
curl -X POST -H "Authorization: Bearer <admin-token>" \
     http://nodepilot-vip/api/secrets/reencrypt
```

- `200 OK` → a clean cutover (`partialSuccess: false`).
- `207 Multi-Status` → skipped rows in `*SkipDetails`, to be fixed manually.

The sweep covers credentials, secret globals, workflow versions, agent MCP secrets,
notification-route secrets, pending dispatch parameters and encrypted runtime settings including
the writer's `.bak.*` rollback files. The three latter families report
`notificationRoutesRewritten/Skipped`, `dispatchParametersRewritten/Skipped` and
`runtimeSettingsFilesRewritten/Skipped`, with corresponding singular `*SkipDetails` arrays.
Runtime counts are files, and runtime skip IDs are the empty GUID. A concurrent database change
is a `ConcurrentUpdate` skip; a bad runtime file stays unchanged. Earlier successful conversions
remain committed on partial success or cancellation; retain the fallback and rerun.

Use a maintenance window and pause secret/configuration changes and restore/import operations.
Start every node with active+legacy providers first; encrypted runtime settings can already use
the fallback during bootstrap. In HA, run the endpoint on each node with its own runtime file:
database rows are shared, but a clean response covers only that node's local files. External
exported backups are not rewritten; retain their recovery key or replace those backups before
retiring the old key permanently.

The same sweep is also available in the UI — **Admin settings → Security → "Re-encrypt secrets"** (admin only; a confirmation dialog, a result toast with the counters, and partial success as an error toast) — and through the CLI: `np secrets reencrypt`.

**Step 3 — remove the legacy configuration:** only once step 2 returns `200` with
`partialSuccess=false`, **all** skip counters including `workflowVersionsSkipped` are zero, and the
`nodepilot.credential.crypto.legacy_reads` counter stays at zero in the follow-up tests.
If a history row was skipped, `Secrets:LegacyProvider` stays configured until the version named is
restored/repaired and a further sweep is clean. After that, remove
`Secrets:LegacyProvider`/`LegacyDpapiScope`/`LegacyMasterKey` and restart.

## Rotating the AES-GCM master key

The same procedure, but with `LegacyProvider=AesGcm` + `LegacyMasterKey={{old-base64}}` in step 1. Steps 2 and 3 are unchanged.

## API

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/secrets/reencrypt` | Admin | Re-encrypts all seven database/runtime secret families above. `200` (clean) or `207` (skipped, with details). |

Audit: `SECRETS_REENCRYPTED`.

## Out of scope (V1)

HashiCorp Vault Transit / Azure Key Vault / KMIP, HSM-backed keys, per-row key IDs, an automatic background sweep. The `ISecretProtector` interface is built so that a network-backed provider can be added in one class plus one DI line.
