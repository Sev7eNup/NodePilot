# Secret-Provider

NodePilot routet alle Verschlüsselung von Secrets at-rest (Machine-Credentials, secret-flagged Globals) durch eine `ISecretProtector`-Abstraktion. Zwei Implementierungen shippen heute; eine dritte (HashiCorp Vault Transit) ist Roadmap.

## Provider-Matrix

| Provider | `Secrets:Provider` | Cluster-portable | Setup |
|---|---|---|---|
| **DPAPI** (default) | `Dpapi` (oder unset) | ❌ machine-bound | `Credentials:DpapiScope=LocalMachine` für Produktion |
| **AES-GCM** | `AesGcm` | ✅ | shared `Secrets:MasterKey` oder `Secrets:MasterKeyFile` (32-Byte base64) auf jedem Node |
| HashiCorp Vault Transit | (noch nicht) | ✅ | Roadmap |

## Startup-Guardrails

- Unbekannte `Secrets:Provider`-Werte (Typo `AesGCMm`) **failen beim Boot** — kein stiller Fallback auf DPAPI.
- `Cluster:Enabled=true` + `Secrets:Provider=Dpapi` (oder default) **failt beim Boot** — DPAPI-Ciphertexte sind machine-bound, der Standby könnte sie nach Failover nicht entschlüsseln.

## DPAPI

```jsonc
"Secrets": { "Provider": "Dpapi" },
"Credentials": { "DpapiScope": "LocalMachine" }
```

`LocalMachine` ist die Produktions-Empfehlung (überlebt Service-Account-Wechsel); `CurrentUser` (Dev-Default) re-bindet beim Account-Wechsel.

## AES-GCM

```jsonc
"Secrets": {
  "Provider": "AesGcm",
  "MasterKeyFile": "C:\\ProgramData\\NodePilot\\secrets\\aesgcm-masterkey.txt"
}
```

`Secrets:MasterKey` bleibt fuer Env-Var-Deployments (`Secrets__MasterKey`) unterstuetzt.
Fuer disk-backed Deployments `Secrets:MasterKeyFile` bevorzugen und die Datei auf die
NodePilot-Service-Identitaet ACL-restricten.

Key generieren und auf jeden Cluster-Node kopieren:

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
# oder
openssl rand -base64 32
```

**Wire-Format:** `[1 byte version=0x01] [12 bytes nonce] [N bytes ciphertext] [16 bytes auth tag]`. Der Versions-Byte ist der Hook für künftige Key-Rotation-Envelopes; heute nur `0x01`.

**Hardening:** Der Master-Key muss AES-GCM verfuegbar sein, muss aber nicht in JSON liegen. Bevorzugt `Secrets__MasterKey` oder ACL-restricted `Secrets:MasterKeyFile`; JSON-backed `Secrets:MasterKey` nur mit restriktiven `appsettings.Production.json`-ACLs und ohne unverschluesselte Backups.

## Migration DPAPI → AES-GCM

**Schritt 1 — beide Provider verkabeln:**

```jsonc
"Secrets": {
  "Provider": "AesGcm",                  // active: Writes, Reads zuerst hier
  "MasterKeyFile": "C:\\ProgramData\\NodePilot\\secrets\\aesgcm-masterkey.txt",
  "LegacyProvider": "Dpapi",             // Fallback für Rows noch im DPAPI-Format
  "LegacyDpapiScope": "LocalMachine"
}
```

**Schritt 2 — Bulk-Re-Encrypt:**

```bash
curl -X POST -H "Authorization: Bearer <admin-token>" \
     http://nodepilot-vip/api/secrets/reencrypt
```

- `200 OK` → clean cutover (`partialSuccess: false`).
- `207 Multi-Status` → übersprungene Rows in `*SkipDetails`, manuell nachpflegen.

Derselbe Sweep ist auch in der UI verfügbar — **Admin-Einstellungen → Security → „Secrets neu verschlüsseln“** (Admin-only; Bestätigungsdialog, Ergebnis-Toast mit den Zählern, Partial Success als Fehler-Toast) — sowie per CLI: `np secrets reencrypt`.

**Schritt 3 — Legacy-Config entfernen** (wenn Step 2 `200` + `nodepilot.credential.crypto.legacy_reads`-Counter null): `Secrets:LegacyProvider`/`LegacyDpapiScope`/`LegacyMasterKey` entfernen, Restart.

## AES-GCM Master-Key rotieren

Gleiches Prozedere, aber `LegacyProvider=AesGcm` + `LegacyMasterKey={{old-base64}}` in Schritt 1. Schritt 2 + 3 unverändert.

## API

| Endpoint | Auth | Zweck |
|---|---|---|
| `POST /api/secrets/reencrypt` | Admin | Bulk-Sweep aller Credentials + secret-Globals durch decrypt→re-encrypt. `200` (clean) oder `207` (skipped). |

Audit: `SECRETS_REENCRYPTED`.

## Out of scope (V1)

HashiCorp Vault Transit / Azure Key Vault / KMIP, HSM-backed Keys, Per-Row Key-ID, automatischer Background-Sweep. Die `ISecretProtector`-Schnittstelle ist so gebaut, dass ein netzwerkgestützter Provider in einer Klasse + DI-Zeile addierbar ist.
