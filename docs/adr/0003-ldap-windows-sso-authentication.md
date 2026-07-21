# ADR 0003 - LDAP and Windows SSO Authentication

**Status:** Superseded by [ADR 0009](0009-enterprise-identity-sessions-and-provisioning.md) - 2026-07-12
**Scope:** Optional external authentication paths beside local BCrypt users.

## Kontext

NodePilot needs to support domain-managed operators without removing the local break-glass admin
path. LDAP simple-bind and Windows Integrated Authentication solve different enterprise access
patterns: LDAP covers password-based domain login, while Negotiate/Kerberos provides browser SSO for
domain-joined Windows clients.

## Entscheidung

NodePilot keeps local BCrypt authentication always available and adds two opt-in external paths:

- LDAP simple-bind through `POST /api/auth/login` when `Authentication:Ldap:Enabled=true`.
- Windows Negotiate/Kerberos through `POST /api/auth/windows` when
  `Authentication:Windows:Enabled=true`.

All paths converge on the same JWT cookie, CSRF token, RBAC model, audit flow, and user table.
External users are JIT-provisioned or refreshed by `(Provider, ExternalId)`, not by mutable display
name alone. LDAP invalid credentials do not silently fall back to local auth for the same username;
infrastructure unavailability can fall back so local admins are not locked out by a domain outage.

## Konsequenzen

- Local-only deployments behave unchanged.
- Domain deployments can use AD groups for global role mapping and folder permissions.
- Username collision handling is explicit and audited.
- Cloud IdP/OIDC remains a separate future decision; LDAP/Negotiate cover AD-centric deployments.

## Referenzen

- [../ldap-windows-sso.md](../ldap-windows-sso.md)
- [../enterprise-features.md](../enterprise-features.md)
