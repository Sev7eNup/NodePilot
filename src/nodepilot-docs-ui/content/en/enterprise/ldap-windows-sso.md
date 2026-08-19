# AD SSO Preview

> **Status: preview.** LDAP/LDAPS and Windows Negotiate are implemented, but only count as enterprise-ready once real AD, Kerberos, LDAPS and NTLM-rejection tests have been run over the production topology. OIDC and SCIM have a separate release gate.

This page describes central sign-in and provisioning. All external sign-in paths are disabled by default. A local break-glass admin account remains necessary for emergencies.

## Sign-in paths

| Path | Endpoint | Default | Purpose |
|---|---|---|---|
| A local password (BCrypt) | `POST /api/auth/login` | `BreakGlassOnly` | Explicitly marked emergency accounts |
| LDAP simple bind over LDAPS | `POST /api/auth/login` | off | Domain sign-in with a user name and password |
| Windows Negotiate/Kerberos | `POST /api/auth/windows` | off | Browser SSO for domain-joined Windows clients |
| OpenID Connect | `GET /api/auth/oidc` | off, release-gated | The general enterprise IdP path with authorization code + PKCE |

All paths create the same server-side revocable session. The JWT cookie contains no group list; the absolute session lifetime is eight hours by default.

## Setting up a single domain

The short path for one domain. Domain users are never imported — they are provisioned on their
first successful login, provided they belong to an allowed group.

**LDAPS is the prerequisite.** NodePilot speaks only LDAPS on port 636 with full certificate
validation against the NodePilot host's Windows certificate store; there is no bypass switch. If
the domain has no CA yet, an Enterprise Root CA is the least-effort route: the DC then enrolls its
own certificate automatically, and Group Policy distributes the root to every domain member, so
the NodePilot host trusts it without manual imports.

```powershell
# On the domain controller, once:
Install-WindowsFeature AD-Certificate -IncludeManagementTools
Install-AdcsCertificationAuthority -CAType EnterpriseRootCa -CACommonName 'corp-CA' -Force
```

**Two groups, one service account.** Access and role are separate: membership in an allowed group
decides whether someone may sign in at all, a role mapping decides what they are.

```powershell
New-ADGroup -Name 'NodePilot-Users'  -GroupScope Global -GroupCategory Security
New-ADGroup -Name 'NodePilot-Admins' -GroupScope Global -GroupCategory Security
Add-ADGroupMember -Identity 'NodePilot-Users'  -Members <user>
Add-ADGroupMember -Identity 'NodePilot-Admins' -Members <user>

# Read the SIDs. Get-ADGroup takes ONE identity, so pipe them rather than passing a list:
'NodePilot-Users','NodePilot-Admins' | Get-ADGroup |
    Select-Object Name, @{n='SID';e={$_.SID.Value}}

# Read-only service account for the authoritative lookups:
New-ADUser -Name 'svc-nodepilot-dir' -AccountPassword (Read-Host -AsSecureString) `
    -Enabled $true -PasswordNeverExpires $true
Get-ADUser 'svc-nodepilot-dir' | Select-Object -ExpandProperty DistinguishedName
```

Then, under Admin settings → *Authentication*: enable LDAP, give one endpoint
(`dc1.corp.example.com:636`), the `BaseDn`, the `UpnSuffix`, the service-bind DN and password, the
`NodePilot-Users` SID as an allowed group, and the `NodePilot-Admins` SID mapped to `Admin`. The
built-in test checks TLS trust, service bind, search base and group resolution against the unsaved
draft before you commit it.

**Configure exactly one endpoint** unless every DC you list is reachable at all times. Directory
access is all-DC consensus, not failover — a second, occasionally-offline DC blocks every AD login
rather than covering for the first.

Users sign in with the bare username (`alice`), not `DOMAIN\alice`; NodePilot appends the UPN
suffix itself. A missing role mapping is not an error — the user is created as a Viewer.

## A safe base configuration

```jsonc
{
  "Authentication": {
    "LocalLoginMode": "BreakGlassOnly",
    "SessionAbsoluteLifetimeHours": 8,
    "MaxAuthorizationStalenessMinutes": 15,
    "Ldap": {
      "Enabled": false,
      "Endpoints": [
        "ldaps://dc01.example.com:636",
        "ldaps://dc02.example.com:636"
      ],
      "Port": 636,
      "UseSsl": true,
      "BaseDn": "DC=example,DC=com",
      "UpnSuffix": "example.com",
      "BindTimeoutSeconds": 5,
      "ServiceBindDn": "CN=svc-nodepilot-ldap,OU=Services,DC=example,DC=com",
      "ServicePassword": "<secret>",
      "AllowedGroupSids": [
        "S-1-5-21-...-512",
        "S-1-5-21-...-1108"
      ],
      "DirectorySyncIntervalMinutes": 5,
      "DirectorySyncMaxConcurrency": 16,
      "GlobalRoleMappings": [
        { "GroupSid": "S-1-5-21-...-512", "Role": "Admin" },
        { "GroupSid": "S-1-5-21-...-1108", "Role": "Operator" }
      ],
      "JitUserDefaultRootRole": null
    },
    "Windows": {
      "Enabled": false,
      "AllowNtlmFallback": false,
      "NtlmDisabledByPolicy": false
    },
    "Oidc": {
      "Enabled": false,
      "Authority": "https://idp.example.com/tenant/v2.0",
      "ClientId": "<client-id>",
      "ClientSecret": "<secret>",
      "DisplayName": "Company account",
      "NameClaimType": "preferred_username",
      "GroupsClaimType": "groups",
      "Scopes": ["openid", "profile", "email"],
      "AllowedGroupIds": ["<admin-group-object-id>", "<operator-group-object-id>"],
      "GlobalRoleMappings": [
        { "GroupId": "<admin-group-object-id>", "Role": "Admin" },
        { "GroupId": "<operator-group-object-id>", "Role": "Operator" }
      ]
    },
    "Scim": {
      "Enabled": false,
      "Authority": "https://idp.example.com/tenant/v2.0",
      "BearerToken": "<random-secret-with-at-least-32-characters>",
      "PreviousBearerToken": null
    }
  }
}
```

- With LDAP **or Windows SSO** enabled, at least one LDAPS endpoint, `BaseDn`, service-bind credentials and one `AllowedGroupSids` SID are required; LDAP password login additionally needs `UpnSuffix`.
- LDAPS with full certificate validation is mandatory: the DC certificate has to validate against the API host's Windows certificate store, and there is no in-app bypass. Plaintext LDAP and StartTLS on port 389 are not a supported enterprise path. LDAP referrals are never followed — every query is answered by the endpoint you deliberately configured.
- `AllowedGroupSids` is the access policy; `GlobalRoleMappings` determines the global role independently of it. Without a role match, `Viewer` applies.
- `DirectorySyncIntervalMinutes` is between 1 and 5 minutes. `DirectorySyncMaxConcurrency` limits a sync pass to 1–32 concurrent service-bind lookups, default 16.
- Windows SSO is Kerberos only. `AllowNtlmFallback=true` is rejected; `NtlmDisabledByPolicy=true` confirms the host/domain policy you have additionally implemented.
- Secrets belong in environment variables or the secret provider, not in a checked-in file.

The entire `Authentication` section is fixed at boot. Saved changes only take effect after a service restart. The LDAP connection test in the admin settings checks the not-yet-saved draft against TLS trust, the service bind and the search base.

In a cluster, authentication is **config-as-code**. `PUT /api/admin/settings/Authentication` returns `409 CLUSTER_CONFIG_AS_CODE_REQUIRED`; identical configuration and secrets have to be rolled out to every node and activated by a cluster restart.

### HA with OIDC

OIDC correlation, nonce and server-side tickets are protected with ASP.NET Core Data Protection. Every HA node therefore needs the same persistent key ring and the same certificate with its private key:

```jsonc
{
  "DataProtection": {
    "KeyRingPath": "\\\\fileserver\\nodepilot\\data-protection-keys",
    "CertificateThumbprint": "<shared-certificate-thumbprint>",
    "SharedKeyRing": true
  }
}
```

`KeyRingPath` has to point at the same persistent storage for all nodes. The certificate has to be in `LocalMachine\My` on every node; it can be separate from the Kestrel TLS certificate. HA+OIDC does not start without those three explicit entries.

## Identity, groups and offboarding

- External identities are found through the immutable pair `(Authority, Subject)`.
- Users are searched by the `userPrincipalName` attribute below the `BaseDn`. A successful bind is not enough: if the UPN attribute on the account is empty, Active Directory still accepts the bind through the implicit form `samAccountName@DNS-domain`, but no findable object exists. The login is then rejected with its own audit reason `ldap_user_object_not_found` — as an account problem, not a directory outage: the circuit breaker is untouched, so a single misconfigured account cannot block LDAP sign-in for everyone. The fix: `Set-ADUser <user> -UserPrincipalName '<user>@<upn-suffix>'`.
- LDAP and Windows use the same AD authority and the user's `objectSid` as the subject. Both paths therefore land on the same NodePilot user for the same person.
- Windows uses only the primary SID from the Kerberos principal. On **every** Windows login, a service bind over LDAPS loads the current, authoritative user and group snapshot; possibly stale PAC groups are not trusted. If the directory lookup is not possible, the login fails closed.
- OIDC uses the validated issuer as the authority and `sub` as the subject.
- Existing users with the same name are not merged automatically. Collisions are rejected and audited.
- Groups exist as server-side membership snapshots and are not written into the JWT or the cookie. Folder group permissions use those snapshots.
- Folder group permissions are namespaced with `PrincipalAuthority` + `PrincipalKey`. AD SIDs and identically named OIDC/SCIM group IDs therefore cannot inherit permissions across providers.
- The AD sync runs every five minutes by default. A group revocation, a deactivation or a tombstone revokes sessions and also blocks scheduled jobs and triggers, at the latest after the maximum authorization staleness of 15 minutes.
- A deactivation set locally by an admin is sticky: neither a healthy AD snapshot nor SCIM `active=true` reactivates the account automatically. Explicit admin reactivation is required for that.
- Deleted external identities are retained as a tombstone and can only be reactivated explicitly by an admin.
- An all-not-found sync across every known AD identity is discarded as a broken `BaseDn`/search permission and creates no mass tombstones. Access still remains fail-closed after the freshness limit.
- With LDAP, Windows, OIDC or SCIM active, an existing database only starts with an active local break-glass admin. An empty installation stays startable for the one-time local bootstrap.

## Kerberos and HAProxy

For Windows SSO, the HTTP SPN has to be registered with `setspn -S` **on the service identity**. If NodePilot runs under a gMSA or a domain account, the computer account's `HOST/` SPN does not cover the service — the ticket is then unreadable to the process, Kerberos fails, and SPNEGO falls back to NTLM silently.

Browsers additionally have to treat the URL as an intranet target: `AuthServerAllowlist` for Edge and Chrome, plus assignment to the "Local intranet" zone by GPO. **A correctly configured client never asks for credentials** — if a sign-in dialog appears, the policy is not in effect.

Important for acceptance testing: a saved password in the Windows credential manager, or an enterprise SSO product that fills in dialogs automatically, makes the sign-in look seamless even though the browser policy is missing. Server-side the two cases are indistinguishable. The proof is therefore only meaningful on a client without such tooling and after a full browser restart.

In front of HAProxy, these additionally apply:

- HTTP/1.1 and persistent connections on both hops;
- `http-reuse never`, so that backend connections are never shared between clients;
- source stickiness during the Negotiate handshake;
- validated backend TLS with a CA, SNI and host-name checking;
- forwarded headers deleted and re-set by the proxy, plus a narrow `ForwardedHeaders:KnownProxies` list.

The complete template is at `deploy/templates/haproxy.cfg.template`.

## OIDC and SCIM 2.0 — a separate release gate

OIDC uses authorization code + PKCE, HTTPS metadata, and issuer, audience, state and nonce validation. Access requires at least one configured `Authentication:Oidc:AllowedGroupIds` group. The discovery endpoint reports `oidcEndpoint` and the configured display name.

Groups from an OIDC token only count if its `iat` is present, at most one minute in the future, and at most `MaxAuthorizationStalenessMinutes` old — 15 minutes maximum. On an explicit group-overage signal, NodePilot may instead use exclusively authority-scoped SCIM memberships whose `LastSeenAt` also lies within that window. A login itself does not extend SCIM freshness.

SCIM provides `ServiceProviderConfig`, `ResourceTypes`, `Schemas`, `/api/scim/v2/Users` and `/api/scim/v2/Groups`. Bearer tokens have to be 32–4096 characters long. For an uninterrupted rotation, the old value stays briefly under `PreviousBearerToken` until the IdP uses the new `BearerToken`; the old slot is then deleted. `Authentication:Scim:Authority` has to match the OIDC authority or falls back to it. When creating a user, `externalId` is mandatory and has to match the OIDC `sub` **exactly and case-sensitively**; it is immutable afterwards.

A SCIM user update confirms no groups and therefore does not renew authorization freshness. For group overage, the IdP has to deliver a complete group snapshot — or a semantically equivalent membership heartbeat that also refreshes unchanged memberships — at least every 15 minutes. That behaviour is part of the SCIM release gate.

OIDC and SCIM stay release-gated until real provider, parallel-JIT, group-overage and offboarding tests have been run. SAML is not part of this target picture.

## API and operations

| Endpoint | Purpose |
|---|---|
| `GET /api/auth/methods` | The active sign-in paths including the OIDC URL and display name |
| `POST /api/auth/login` | A local password or LDAP |
| `POST /api/auth/windows` | Negotiate/Kerberos |
| `GET /api/auth/oidc` / `GET /api/auth/oidc/callback` | The OIDC browser flow |
| `POST /api/admin/settings/test/ldap` | An LDAPS/bind/search test of the draft |
| `GET /healthz/ready` | Database readiness; it deliberately contains no directory check |
| `GET /healthz/directory` | A separate LDAPS/service-bind health check across all DCs; a failed secondary yields `Degraded` |
| `/api/scim/v2/*` | SCIM discovery, users and groups |

**The sign-in name in the form.** `alice`, `DOMAIN\alice` and `alice@example.com` are all normalized to the same UPN; a forward slash (`DOMAIN/alice`) is **not** recognized and stays part of the name. Every failure leaves an audit entry with a `reason`: `ldap_invalid_credentials` (wrong password/UPN), `ldap_user_object_not_found` (the UPN attribute is missing), `no_allowed_directory_group` (the group gate), `pre_jit_account_throttle` (five attempts per 15 minutes) or `infrastructure_failure` (the directory is unreachable, additionally HTTP 503).

At the IdP, register `https://<nodepilot>/signin-oidc` as the redirect URI.
`/api/auth/oidc/callback` is only the internal landing URL after the OIDC handler has validated the
code, state, nonce, issuer and signature.

Before **AD SSO Preview** can be promoted, real tests over the target topology have to demonstrate: LDAP and Windows map the same SID to the same user, the Windows path ignores PAC groups in favour of the LDAPS snapshot, LDAPS rejects invalid certificates, Kerberos works through HAProxy, NTLM is rejected, and offboarding takes effect within 15 minutes. For OIDC/SCIM, HA failover with a shared data-protection key ring and complete membership heartbeats within 15 minutes are additionally part of the release gate.
