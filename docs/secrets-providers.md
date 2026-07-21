# Secret Protector Providers

NodePilot routes all encryption of secrets at rest (machine credentials, secret-flagged
global variables) through a single `ISecretProtector` abstraction. Two implementations
ship today; a third (HashiCorp Vault Transit) is on the roadmap.

## Why this exists

Pre-abstraction, `CredentialStore` and `GlobalVariableStore` directly called Windows DPAPI.
DPAPI-encrypted blobs are **machine-bound**: a credential encrypted on Node A cannot be
decrypted on Node B. That makes active/passive HA effectively impossible without
re-entering every credential after a failover.

The abstraction decouples the encrypt/decrypt contract from the wire format and DI-routes
the active provider, so swapping DPAPI for AES-GCM (or later HashiCorp Vault) is a
configuration change rather than a code change.

## Provider matrix

| Provider | `Secrets:Provider` | Cluster-portable | Operator setup |
|---|---|---|---|
| **DPAPI** (default) | `Dpapi` (or unset) | ❌ machine-bound | `Credentials:DpapiScope=LocalMachine` recommended for production |
| **AES-GCM** | `AesGcm` | ✅ | shared `Secrets:MasterKey` or `Secrets:MasterKeyFile` (32-byte base64) on every node |
| **HashiCorp Vault Transit** | (not yet) | ✅ | tracked in plan `enterprise-vault-secrets.md` |

### Startup guardrails

- Unknown `Secrets:Provider` values (e.g. typo `AesGCMm`) **fail at startup**, not silently
  fall back to DPAPI. Allowed values: `Dpapi`, `AesGcm`, or empty/missing (defaults to DPAPI).
- `Cluster:Enabled=true` combined with `Secrets:Provider=Dpapi` (or empty/default)
  **fails at startup** with an actionable error. DPAPI ciphertexts are machine-bound so
  the standby node could never decrypt them after failover; clustering with DPAPI is a
  silently broken configuration that we refuse to boot.

## DPAPI (default)

```jsonc
"Secrets": { "Provider": "Dpapi" },
"Credentials": { "DpapiScope": "LocalMachine" }
```

Same behaviour as before this abstraction shipped. `LocalMachine` is the production
recommendation; `CurrentUser` (the dev default) re-binds if the service account changes.

## AES-GCM (cluster-portable)

```jsonc
"Secrets": {
  "Provider": "AesGcm",
  "MasterKeyFile": "C:\\ProgramData\\NodePilot\\secrets\\aesgcm-masterkey.txt"
}
```

`Secrets:MasterKey` is still supported for environment-variable deployments
(`Secrets__MasterKey`). For disk-backed deployments, prefer `Secrets:MasterKeyFile` and
ACL the file to the NodePilot service identity only.

Generate the master key once and copy it to every cluster node:

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
```
or
```bash
openssl rand -base64 32
```

**Wire format** (binary, persisted as-is in the existing `byte[]` column):
```
[1 byte version=0x01] [12 bytes nonce] [N bytes ciphertext] [16 bytes auth tag]
```

The version byte is reserved for future key rotation envelopes (e.g. multi-key
"key-id + ciphertext" wrappers). Today only `0x01` is accepted.

### Hardening

The master key must be available to AES-GCM, but it does not have to live in JSON.
Operators MUST:

- Restrict `appsettings.Production.json` ACLs (`icacls /grant svc-nodepilot:R` only,
  remove `BUILTIN\Users`).
- Exclude the file from any backup that's not encrypted at rest.
- Prefer environment variable `Secrets__MasterKey` or ACL-restricted `Secrets:MasterKeyFile`
  over JSON-backed `Secrets:MasterKey`.
- Rotate the key via the migration path described in
  [§ Rotating the AES-GCM master key](#rotating-the-aes-gcm-master-key) below — set
  `Secrets:LegacyMasterKey` or `Secrets:LegacyMasterKeyFile` alongside the new key, run
  `POST /api/secrets/reencrypt`,
  then drop the legacy entries on the next restart.

The startup hardening warning emits a SECURITY log line on boot whenever a plaintext
master key is detected, so operators get a daily reminder if they forget to harden.

## Migration from DPAPI to AES-GCM

The supported migration path uses a temporary **MigratingSecretProtector** wrapper that
keeps both providers live during the rotation window. After a single bulk-rewrite call,
the deployment runs pure-AES-GCM and the legacy DPAPI config can be removed.

### Step 1 — wire both providers

```jsonc
"Secrets": {
  "Provider": "AesGcm",                  // active: writes use this, reads try this first
  "MasterKeyFile": "C:\\ProgramData\\NodePilot\\secrets\\aesgcm-masterkey.txt",

  "LegacyProvider": "Dpapi",             // fallback for rows still in DPAPI format
  "LegacyDpapiScope": "LocalMachine"     // optional, defaults to CurrentUser
}
```

Boot log emits `[Secrets] Migrating secret protector enabled: active=AesGcm, legacy=Dpapi.`

### Step 2 — bulk re-encrypt

```bash
curl -X POST -H "Authorization: Bearer <admin-token>" \
     http://nodepilot-vip/api/secrets/reencrypt
```

The same sweep is available in the UI — **Admin settings → Security → "Re-encrypt
secrets"** (Admin-only card; confirm dialog, result toast with the rewritten counts,
partial success surfaces as an error toast) — and via the CLI: `np secrets reencrypt`.

Clean-success response (status `200 OK`):
```json
{
  "credentialsRewritten": 47,
  "credentialsSkipped": 0,
  "credentialSkipDetails": [],
  "globalSecretsRewritten": 12,
  "globalSecretsSkipped": 0,
  "globalSecretSkipDetails": [],
  "partialSuccess": false
}
```

Partial-success response (status `207 Multi-Status`) — at least one row could not be
decrypted under any configured protector:
```json
{
  "credentialsRewritten": 5,
  "credentialsSkipped": 1,
  "credentialSkipDetails": [
    { "id": "abc...", "name": "broken-svc", "reason": "CryptographicException" }
  ],
  "globalSecretsRewritten": 3,
  "globalSecretsSkipped": 1,
  "globalSecretSkipDetails": [
    { "id": "def...", "name": "STRIPE_KEY", "reason": "FormatException" }
  ],
  "partialSuccess": true
}
```

The endpoint walks every credential password and every secret-flagged global, decrypts
through the migrating wrapper (active first, falls back to legacy when the bytes don't
parse under active), and re-encrypts under the active provider. Successfully migrated
rows are committed regardless of skip outcomes — a partial sweep still moves the
deployment forward. **`partialSuccess=true` (status 207) is the operator's signal to
re-enter the listed rows manually before dropping the legacy config in Step 3.**

CI / Ansible can branch on the status line directly: `200` = clean cutover, `207` =
manual follow-up needed for the named rows.

### Step 3 — drop the legacy config

Pre-conditions: response from Step 2 was `200 OK` with `partialSuccess=false` AND the
`nodepilot.credential.crypto.legacy_reads` counter is zero (every read now hits the
active provider directly). If Step 2 returned `207`, deal with the rows in
`*SkipDetails` first — re-enter them through the credentials/global-variables UI, then
re-run Step 2 until clean.

Once clean, remove the `Secrets:LegacyProvider` / `Secrets:LegacyDpapiScope` /
`Secrets:LegacyMasterKey` keys and restart. The deployment is now pure-active-provider.

### Failure modes

| Symptom | Cause | Fix |
|---|---|---|
| `legacy_reads` keeps climbing after the sweep | New rows being written somewhere in the legacy format | Investigate — should not happen after Step 2; possibly a parallel deployment branch still running DPAPI |
| `CryptographicException: Decrypt failed under both protectors` | Row written under a third provider, OR ciphertext corrupted | Re-enter the affected secret manually; check `LegacyDpapiScope` matches what wrote the row |
| `Re-encrypt skipped credential 'X'` warning during Step 2 | Single row's ciphertext is unrecoverable | Re-enter that credential; sweep continues for the rest |

### Rotating the AES-GCM master key

Same procedure with `LegacyProvider=AesGcm` + `LegacyMasterKey={{old-base64}}` instead of
DPAPI in Step 1. Step 2 + 3 unchanged.

## Operator checklist

- [ ] `Secrets:Provider` set explicitly in `appsettings.Production.json` (not defaulted).
- [ ] `LocalMachine` DPAPI scope, OR shared AES-GCM key, depending on cluster vs single-node.
- [ ] `appsettings.Production.json` is `icacls` / `chmod 600`-restricted to the service account.
- [ ] Master key (if AES-GCM) backed up to a separate secret store — without it, encrypted
      DB rows are permanently unrecoverable.
- [ ] Boot log (logger category `Secrets`) shows the expected provider line. Two shapes:
      - Single provider, no migration: `Secret protector enabled. Provider: AesGcm.`
      - Migration window with legacy fallback: `Migrating secret protector enabled: active=AesGcm, legacy=Dpapi. Run POST /api/secrets/reencrypt then remove Secrets:LegacyProvider once the legacy_reads counter is zero.`
- [ ] After cluster-mode switch, smoke-test one credential decrypt on each node.

## Bewusst nicht in V1

| Feature | Status |
|---|---|
| **HashiCorp Vault Transit / KMIP / Cloud-KMS providers** | Roadmap. The `ISecretProtector` abstraction is built so adding a network-backed provider is a single class + DI line; today only the two on-host providers (DPAPI / AES-GCM) ship. |
| **HSM-backed AES key** | Roadmap. The master key is a 32-byte software value at rest. PKCS#11 / Windows CNG-backed keys are V2. |
| **Per-row key-id / multi-key decrypt** | Roadmap. The 1-byte version prefix in the AES-GCM envelope is the hook for this; today only `0x01` is accepted. Until that's wired, key rotation goes through the `LegacyProvider` migration path. |
| **Automatic background re-encrypt sweep** | Out of scope. The admin-triggered `POST /api/secrets/reencrypt` is the supported path; an automatic sweep would mask the audit trail of who rotated when. |
