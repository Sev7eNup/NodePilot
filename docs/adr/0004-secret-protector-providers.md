# ADR 0004 - Secret Protector Providers

**Status:** Implemented - 2026-05-09
**Scope:** Encryption provider selection for secrets at rest.

## Kontext

Credentials and secret global variables must be encrypted at rest. The original DPAPI-only approach
is safe for a single Windows node, but DPAPI ciphertext is machine-bound. That makes failover to a
different node impossible without re-entering secrets.

## Entscheidung

All at-rest secret encryption goes through the `ISecretProtector` abstraction. The active provider
is selected by configuration:

- `Dpapi` or empty: default single-node provider.
- `AesGcm`: cluster-portable provider using a shared 32-byte master key.
- migrating mode: temporary dual-read/single-write wrapper for bulk re-encryption.

Unknown provider values fail startup. `Cluster:Enabled=true` combined with DPAPI also fails startup,
because that deployment would only break at failover time.

## Konsequenzen

- Single-node installs keep DPAPI by default.
- Clustered installs must deliberately provision a shared AES-GCM master key.
- Provider migration is explicit and testable through the re-encryption endpoint.
- Future external providers, such as Vault Transit, can plug into the same abstraction without
  changing callers.

## Referenzen

- [../secrets-providers.md](../secrets-providers.md)
- [0002-active-passive-ha.md](0002-active-passive-ha.md)
