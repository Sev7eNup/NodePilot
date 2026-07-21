# ADR 0009 - Enterprise Identity, Sessions and Provisioning

**Status:** Accepted (AD SSO Preview) - 2026-07-12
**Supersedes:** [ADR 0003](0003-ldap-windows-sso-authentication.md)

## Kontext

LDAP and Windows SSO originally used provider-specific user fields, long-lived authorization claims and optional local-account linking. That model cannot guarantee bounded offboarding, cross-protocol identity convergence or safe operation behind an HA proxy. Cloud identity also requires a standards-based login and provisioning path.

## Entscheidung

- Every external login resolves an immutable `ExternalIdentity(Authority, Subject)`. LDAP and Windows use the canonical AD `objectSid`, so both protocols map one person to one NodePilot user. Existing users are never merged by username.
- JWTs identify a revocable server-side `AuthSession`; they do not carry directory groups. Refresh atomically rotates the current JTI, and account, tombstone, session and authorization freshness are checked for HTTP, SignalR and execution dispatch.
- `DirectoryMembership` snapshots are authority-scoped. AD is refreshed every one to five minutes; external authorization may never be older than 15 minutes. Deactivation or group removal revokes sessions and stops pending or active automated execution.
- AD admission requires an explicit group allowlist, LDAPS with certificate validation and service-bind credentials. Local authentication defaults to `BreakGlassOnly`; Windows SSO is Kerberos-only and requires an operator attestation that NTLM is blocked by host/domain policy.
- OIDC uses Authorization Code + PKCE and a server-side temporary ticket store. SCIM 2.0 provisions users and groups under the same issuer authority. Both remain separately release-gated; SAML is out of scope.
- Authentication scheme changes are restart-required. Until real AD/Kerberos/LDAPS tests through the production proxy pass, the product label is **AD SSO Preview**, not enterprise-ready.

## Konsequenzen

- Tokens remain below browser cookie limits even for users with hundreds of groups.
- Authorization reduction has a hard 15-minute upper bound when the configured directory/IdP feed remains authoritative and available; infrastructure failure fails closed after the last valid snapshot expires.
- Case-sensitive OIDC/SCIM subjects are compared ordinally in application code. Database collations that cannot store case-distinct identifiers may reject the second identifier rather than alias accounts.
- Deployments must validate SPNs, browser policy, NTLM rejection, LDAPS trust, IdP claims and SCIM cadence in their own environment before removing the Preview gate.

## Referenzen

- [../ldap-windows-sso.md](../ldap-windows-sso.md)
- [../enterprise-features.md](../enterprise-features.md)
- [../../deploy/templates/haproxy.cfg.template](../../deploy/templates/haproxy.cfg.template)
