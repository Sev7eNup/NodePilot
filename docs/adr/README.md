# Architecture Decision Records (ADRs)

An ADR captures a **decision of lasting architectural consequence** — the context that forced
it, what was decided, and the trade-offs — so the *why* survives long after the diff that
implemented it. ADRs are append-only history: supersede an old one with a new record rather
than rewriting it (amend in place only for small corrections, as ADR 0007 does).

## Index

| ADR | Title | Status |
|-----|-------|--------|
| [0001](0001-system-configuration-backup-restore.md) | System Configuration Backup & Restore | Implemented |
| [0002](0002-active-passive-ha.md) | Active/Passive HA | Implemented |
| [0003](0003-ldap-windows-sso-authentication.md) | LDAP and Windows SSO Authentication | Superseded by 0009 |
| [0004](0004-secret-protector-providers.md) | Secret Protector Providers | Implemented |
| [0005](0005-mcp-server-http-only-client.md) | MCP Server as HTTP-Only Client | Implemented |
| [0006](0006-trigger-only-workflow-roots.md) | Trigger-Only Workflow Roots | Implemented |
| [0007](0007-api-validation-and-error-contract.md) | API Validation and Error Contract | Accepted (amended 2026-07-07) |
| [0008](0008-modular-system-alert-sources.md) | Modular System-Alert Sources, Observations & Policies | Accepted |
| [0009](0009-enterprise-identity-sessions-and-provisioning.md) | Enterprise Identity, Sessions and Provisioning | Accepted (AD SSO Preview) |
| [0010](0010-single-process-hosting.md) | Single-Process Hosting Topology | Accepted |

## When does a decision warrant an ADR?

Write one when a decision is **hard to reverse, cross-cutting, or non-obvious** — the kind a
future contributor would otherwise have to reverse-engineer or accidentally undo. Concretely,
an ADR is warranted when the decision does any of:

- **Sets a boundary or contract** other code must respect (dependency direction, an API error
  contract, a module's public seam).
- **Chooses one mechanism where several were viable** and the choice constrains future work
  (HTTP-only client vs. shared project, one secret-provider strategy, one graph-root rule).
- **Introduces a durable invariant** a guard test now enforces (so the ADR explains what the
  test protects and why).
- **Is expensive to reverse** — a schema/data-model shape, a hosting/HA model, an auth model.

It is **not** warranted for routine feature work, bug fixes, refactors that preserve behavior,
or choices captured well enough by a `docs/*.md` page. When in doubt: if you'd want to explain
the *why* to a reviewer who wasn't in the room, and that why isn't obvious from the code, write
the ADR. Subsystem how-to detail belongs in `docs/`; the load-bearing *decision* belongs here.

## Writing one

1. Copy [`0000-template.md`](0000-template.md) to `NNNN-short-title.md` (next free number,
   zero-padded to four digits).
2. Fill in Status/Scope and the Kontext / Entscheidung / Konsequenzen / Referenzen sections.
3. Add a row to the index table above.
4. Reference the ADR from the code it governs and any guard test that enforces it.
