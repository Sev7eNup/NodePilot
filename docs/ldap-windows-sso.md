# Enterprise Authentication: AD SSO Preview

> **Status: AD SSO Preview.** The implementation is hardened and covered by automated tests, but the label may only change after a real Active Directory field test has verified Kerberos and LDAPS through the production HAProxy path, including rejection of NTLM.

NodePilot supports four login paths and one provisioning path:

| Path | Endpoint | Default | Purpose |
|---|---|---:|---|
| Local BCrypt | `POST /api/auth/login` | `BreakGlassOnly` | Bootstrap and explicitly marked recovery accounts |
| AD over LDAPS | `POST /api/auth/login` | off | Password-based domain login |
| Windows Kerberos | `POST /api/auth/windows` | off | Integrated browser SSO |
| OIDC Code + PKCE | `GET /api/auth/oidc` | off | General enterprise IdP login |
| SCIM 2.0 | `/api/scim/v2` | off | IdP-driven user and group provisioning |

All login paths issue the same server-side, revocable NodePilot session. Directory groups are never placed in the NodePilot JWT.

## Security invariants

- External accounts are keyed by immutable `ExternalIdentity(Authority, Subject)` values, never by username or display name.
- LDAP and Windows use the same canonical AD `objectSid` subject under the same AD authority. Switching between both protocols therefore resolves the same NodePilot user.
- Existing users are never merged automatically. Any username collision fails closed and is audited; `AllowLocalUserAutoLink=true` is rejected at startup and by the Admin settings validator.
- AD access requires membership in at least one configured `AllowedGroupSids` entry.
- LDAP uses port 636/LDAPS and full certificate validation against the API host's Windows certificate store — the DC certificate must chain to a CA the host trusts; there is no in-app bypass. Plaintext simple bind is refused unconditionally. LDAP referrals are never chased; every query is answered by the deliberately configured endpoint.
- Multi-DC directory access is **all-DC consensus, not login failover**. The password bind tries the configured endpoints in order, but the authoritative SID/group lookup that follows every login queries *every* configured DC and requires them to agree on existence, enabled state and group membership. An unreachable or disagreeing DC makes the login fail closed (HTTP 503) — it does not fail over to a surviving DC. Configure only DCs you expect to be reachable together; a single always-on DC is the simplest correct topology. (Destructive offboarding likewise requires all-DC agreement so a partial outage never mass-revokes access.)
- Windows SSO is Kerberos-only. `AllowNtlmFallback` must remain `false`, and startup requires `NtlmDisabledByPolicy=true` as an operator attestation that host/domain policy rejects incoming NTLM.
- Memberships are authority-scoped server-side snapshots. AD sync runs every one to five minutes, and external authorization is rejected once the last authoritative snapshot is more than 15 minutes old.
- Deactivation, tombstoning or access-group removal revokes sessions and stops pending, running or paused executions belonging to the effective user, including schedules, webhooks and external triggers.
- Local login defaults to `BreakGlassOnly`; only bootstrap and explicitly marked break-glass local accounts can use a password in that mode. Every successful emergency login emits the dedicated `BREAK_GLASS_LOGIN_SUCCESS` audit action for SIEM alerting.
- Authentication scheme changes are process-start decisions. Restart the NodePilot service after saving this section.

## Safe baseline configuration

The shipped configuration keeps all external paths disabled. The following example shows the required shape before enabling them:

```jsonc
{
  "Authentication": {
    "LocalLoginMode": "BreakGlassOnly",
    "SessionAbsoluteLifetimeHours": 8,
    "MaxAuthorizationStalenessMinutes": 15,

    "Ldap": {
      "Enabled": true,
      "Endpoints": [
        "dc01.contoso.example:636",
        "dc02.contoso.example:636"
      ],
      "Port": 636,
      "UseSsl": true,
      "BaseDn": "DC=contoso,DC=example",
      "UpnSuffix": "contoso.example",
      "BindTimeoutSeconds": 5,
      "ServiceBindDn": "CN=svc-nodepilot-directory,OU=Service Accounts,DC=contoso,DC=example",
      "ServicePassword": "<secret>",
      "AllowedGroupSids": [
        "S-1-5-21-111111111-222222222-333333333-1200"
      ],
      "DirectorySyncIntervalMinutes": 5,
      "GlobalRoleMappings": [
        {
          "GroupSid": "S-1-5-21-111111111-222222222-333333333-1201",
          "Role": "Admin"
        },
        {
          "GroupSid": "S-1-5-21-111111111-222222222-333333333-1202",
          "Role": "Operator"
        }
      ],
      "JitUserDefaultRootRole": null
    },

    "Windows": {
      "Enabled": true,
      "AllowNtlmFallback": false,
      "NtlmDisabledByPolicy": true
    },

    "Oidc": {
      "Enabled": false,
      "Authority": "https://idp.contoso.example/tenant/v2.0",
      "ClientId": "<client-id>",
      "ClientSecret": "<secret>",
      "DisplayName": "Contoso SSO",
      "NameClaimType": "preferred_username",
      "GroupsClaimType": "groups",
      "Scopes": ["openid", "profile", "email"],
      "AllowedGroupIds": ["<idp-group-id>"],
      "GlobalRoleMappings": [
        { "GroupId": "<idp-admin-group-id>", "Role": "Admin" }
      ]
    },

    "Scim": {
      "Enabled": false,
      "Authority": "https://idp.contoso.example/tenant/v2.0",
      "BearerToken": "<random-secret-with-at-least-32-characters>",
      "PreviousBearerToken": null
    }
  },

  "ForwardedHeaders": {
    "KnownProxies": ["10.20.30.40"]
  }
}
```

When Windows SSO is enabled without password-based LDAP login, the `Ldap` directory settings are still required. Windows supplies the initial SID, while LDAPS supplies authoritative background refresh, group removal and account deactivation.

Use the Admin settings connection test before restarting. Secrets returned to the UI are masked and are preserved when the unchanged placeholder is submitted.

## Identity and migration behavior

The canonical AD identity authority is `urn:nodepilot:identity:active-directory`; its subject is the canonical SID string. LDAP reads `objectSid`, and Windows requires `ClaimTypes.PrimarySid`. A missing or malformed primary SID is rejected—`NameIdentifier` is not accepted as an identity substitute.

The schema migration creates `ExternalIdentities`, moves legacy provider identifiers into the new model and enforces uniqueness of `(Authority, Subject)`. Legacy LDAP GUID identities are upgraded to the AD SID on a verified login. If migration or parallel JIT discovers an ambiguous mapping, NodePilot refuses the login instead of merging accounts. A unique-index race reloads the winning row and proceeds only when it represents the exact same canonical identity.

Provider issuer and subject values for OIDC/SCIM are opaque and compared ordinally after rejecting surrounding whitespace. On a case-insensitive database, a collation alias is rejected rather than being allowed to resolve another account.

## Sessions, membership and offboarding

`AuthSession` is the authorization boundary behind every NodePilot token:

- absolute lifetime defaults to eight hours;
- `jti`, user id, security stamp and session id are mandatory;
- refresh compares and rotates `CurrentJti` atomically, so a token can only be refreshed once;
- logout, password/security-stamp change, tombstone, deactivation and directory authorization reduction revoke the session immediately;
- HTTP, SignalR and execution dispatch independently enforce the current session and user state.

`DirectoryMembership` stores `(UserId, Authority, GroupKey)` plus the time it was authoritatively observed. Folder group grants store the same authority alongside the provider-stable key: AD uses the canonical AD authority plus a SID; OIDC/SCIM uses the exact HTTPS issuer plus the opaque group id. Identical keys from different providers therefore cannot collide or inherit each other's grants.

The directory worker runs at `DirectorySyncIntervalMinutes` (maximum five). Authorization has a separate hard ceiling of `MaxAuthorizationStalenessMinutes` (maximum 15). If every configured DC confirms that one user no longer exists while another known identity anchors the configured search scope, the missing user is tombstoned. An all-known-users-not-found pass is rejected as a likely wrong `BaseDn` or search-permission failure and never mass-tombstones the tenant. If any DC is unavailable, a single "not found" response is also treated as infrastructure uncertainty; access still fails closed once the last valid snapshot exceeds the configured ceiling.

When any external provider or SCIM is enabled, an existing database must contain an active local Admin with a password and `IsBreakGlass=true`. Startup fails before serving SSO traffic if that recovery invariant is missing. An empty database is allowed to start only so the one-shot local bootstrap can create that account.

For OIDC group overage, saved memberships may be used only when the token explicitly signals overage and the issuer-matching snapshot is no older than the authorization ceiling. Missing group claims without an overage signal are denied. A SCIM user update does not refresh group freshness; the IdP must deliver authoritative group membership changes within the same 15-minute bound.

## Kerberos and HAProxy

Register the public HTTP SPN exactly once on the identity running NodePilot:

```powershell
setspn -S HTTP/nodepilot.contoso.example CONTOSO\svc-nodepilot$
setspn -Q HTTP/nodepilot.contoso.example
```

Configure browsers to treat the HTTPS origin as an intranet/Kerberos site. Disable incoming NTLM through domain or host policy before setting `NtlmDisabledByPolicy=true`; the flag is an attestation, not the enforcement mechanism.

Negotiate is connection-scoped. The production proxy must preserve a frontend/backend HTTP/1.1 connection pair and must never reuse an authenticated backend connection for a different client. The checked-in [HAProxy template](../deploy/templates/haproxy.cfg.template) enforces the required baseline:

```haproxy
defaults
    mode http
    option http-keep-alive

frontend nodepilot_frontend
    bind *:443 ssl crt /etc/haproxy/nodepilot.pem alpn http/1.1
    http-request del-header Forwarded
    http-request del-header X-Forwarded-For
    http-request del-header X-Forwarded-Proto
    option forwardfor header X-Forwarded-For
    http-request set-header X-Forwarded-Proto https
    default_backend nodepilot_active

backend nodepilot_active
    option httpchk
    http-check send meth GET uri /healthz/leader hdr Host nodepilot-backend.contoso.example
    http-check expect status 200
    default-server ssl verify required ca-file /etc/haproxy/contoso-ca.pem alpn http/1.1
    http-reuse never
    balance source
    hash-type consistent
    server node-a 10.20.30.11:443 check sni str(nodepilot-backend.contoso.example) check-sni nodepilot-backend.contoso.example verifyhost nodepilot-backend.contoso.example
    server node-b 10.20.30.12:443 check backup sni str(nodepilot-backend.contoso.example) check-sni nodepilot-backend.contoso.example verifyhost nodepilot-backend.contoso.example
```

Both backend certificates must contain the configured backend name as a SAN. Keep `verify required`, `verifyhost`, SNI and the CA file enabled. Add only the proxy transport address to `ForwardedHeaders:KnownProxies`; client-supplied forwarding headers are stripped and regenerated at the proxy trust boundary.

## OIDC and SCIM release gate

OIDC uses Authorization Code with PKCE, state, nonce, issuer, audience and signature validation. Its short-lived external authentication ticket is encrypted and stored server-side; the browser receives only an opaque handle, keeping the cookie small even when an IdP sends hundreds of groups.

Register the OpenID Connect handler's redirect URI at the IdP as:

```text
https://nodepilot.contoso.example/signin-oidc
```

`/api/auth/oidc/callback` is NodePilot's internal landing URL after the framework has
validated and consumed the remote callback; it is not the redirect URI sent to the IdP.

SCIM exposes standard `ServiceProviderConfig`, `ResourceTypes` and `Schemas` discovery plus Users and Groups resources below:

```text
https://nodepilot.contoso.example/api/scim/v2
```

SCIM bearer tokens must contain 32–4096 characters and should be generated randomly. For zero-downtime rotation, move the old value to `PreviousBearerToken`, install the new value as `BearerToken`, switch the IdP, then clear `PreviousBearerToken`; both slots are accepted only while explicitly configured. SCIM user/group mutations are serialized with a database transaction, protect the last active admin, audit changes and revoke affected sessions/executions. The SCIM `Authority` should exactly match the OIDC issuer so provisioned subjects and login subjects share one namespace.

OIDC and SCIM are a separate enterprise release gate. Validate the target IdP's issuer/subject case semantics, group-overage signal, group-removal latency, deprovisioning and full re-provisioning after disaster recovery. SAML is intentionally out of scope.

## Discovery, health and troubleshooting

Anonymous login discovery returns:

```json
{
  "local": true,
  "ldap": true,
  "windows": true,
  "windowsEndpoint": "/api/auth/windows",
  "oidc": true,
  "oidcEndpoint": "/api/auth/oidc",
  "oidcDisplayName": "Contoso SSO"
}
```

`local` reflects `LocalLoginMode`; it is not unconditionally enabled. The login form remains available for LDAP even when local passwords are disabled.

Operational checks:

- `GET /healthz/live` verifies process liveness.
- `GET /healthz/ready` checks database readiness only, preserving the local break-glass path during a DC outage.
- `GET /healthz/directory` separately checks LDAPS, service bind and directory readability across every configured DC; loss of only a secondary reports `Degraded` and lists healthy/failed endpoints.
- `GET /healthz/leader` is the HAProxy active-node probe.
- Admin settings exposes an authentication connection test for current LDAPS endpoints.
- Authentication configuration validation fails startup/saves for plaintext or malformed LDAP endpoints, missing allowlists/service bind, excessive sync/staleness intervals, auto-link, NTLM fallback, mismatched OIDC/SCIM authorities, incomplete OIDC or weak SCIM tokens.

Common failures:

| Symptom | Check |
|---|---|
| Repeating `401 Negotiate` | SPN ownership, browser intranet policy, HTTP/1.1 persistence and `http-reuse never` |
| NTLM succeeds | Host/domain policy is not actually blocking NTLM; do not attest `NtlmDisabledByPolicy` yet |
| LDAPS connection fails or health is degraded | DC SAN/hostname, trust chain, port 636, service-bind DN, firewall and every configured endpoint (all must be reachable — access is all-DC consensus, not failover) |
| Startup refuses an existing SSO database | Provision or restore an active local Admin with password and `IsBreakGlass=true` before enabling external authentication |
| LDAP and Windows create different users | LDAP must return `objectSid`; Windows must supply the same canonical `PrimarySid` |
| Login works, then fails within 15 minutes | Directory/SCIM membership source is stale or unavailable; this is fail-closed behavior |
| Existing local username is refused | Expected collision protection; migrate explicitly, never via auto-link |
| OIDC callback redirects with `access_not_assigned` | No allowed group, missing group claim without overage signal, or stale SCIM snapshot |

## Required field-test before production status

Run this matrix against real AD and production-equivalent HAProxy—not a mock directory:

1. Validate every configured LDAPS endpoint with full certificate verification. Then decide the multi-DC contract explicitly: the current semantics are **all-DC consensus, not login failover** — the bind fails over across endpoints, but the authoritative post-bind lookup requires every DC to agree, so a primary-DC outage blocks external logins (fail-closed 503) instead of failing over. Either accept and document that (single-DC or always-both topology), or implement a quorum/degraded login path before sign-off.
2. Log in the same person once through LDAP and once through Windows; verify both use the same NodePilot user id and AD SID identity.
3. Capture the Windows handshake through HAProxy and verify Kerberos succeeds over persistent HTTP/1.1 connections.
4. Remove the Kerberos ticket or force an NTLM-only client; verify NodePilot access is denied and no NTLM-authenticated session is created.
5. Remove the allowed AD group and then disable the AD account; in both cases confirm HTTP, SignalR, schedules, webhooks, external triggers and active execution stop no later than 15 minutes.
6. Interrupt one DC while the other answers "not found"; verify the account is not incorrectly tombstoned.
7. Run parallel first-logins for one new AD identity and verify exactly one user/identity is created.
8. Exercise OIDC with 500 groups and verify the NodePilot session cookie remains below 3,800 bytes.
9. Exercise SCIM user/group create, update, removal, last-admin protection and full re-provisioning.
10. Restart both HA nodes after changing authentication settings and verify discovery, readiness and provider-specific Admin UI state.

Until all applicable checks pass and evidence is retained, use the status **AD SSO Preview**.

A local pre-check harness for the LDAP password path lives in [`scripts/ldap-testdc/`](../scripts/ldap-testdc/README.md): a Samba AD DC in Docker with real LDAPS and a throwaway CA, a 13-step login suite (nested `tokenGroups`, role mapping, group gate, JIT, H-17, admin probe) plus an outage drill — validated 2026-07-24. It exercises the real `SystemLdapConnectionAdapter` wire path end to end but does **not** replace this field test: it covers no Kerberos/Negotiate handshake, no multi-DC consensus, and no HAProxy path. Note that with a single configured endpoint the harness cannot exercise item 1 above — and per the current reconciliation semantics, a primary-DC failure blocks external logins rather than failing over (see `ReconcileEndpointResults`); resolve that design-vs-doc tension before running the field test.
