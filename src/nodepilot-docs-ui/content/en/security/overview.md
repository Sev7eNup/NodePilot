# Security model

NodePilot enables its security checks by default. `appsettings.Development.json` relaxes selected checks for local development only.

## Authentication & sessions

- **Server-side sessions:** eight hours absolute lifetime by default, individually revocable. JWTs carry the session ID, the security stamp and a `jti`; groups stay out of the JWT and the cookie.
- **Local BCrypt passwords:** the production default is `BreakGlassOnly`; only explicitly marked emergency accounts may sign in locally.
- **Password length per path:** local accounts stop at 72 UTF-8 bytes (BCrypt silently truncates beyond that). Directory passwords are governed by the directory: NodePilot never truncates them and only rejects a login payload beyond 256 bytes — the AD maximum. A long AD passphrase therefore always reaches the bind.
- **External paths:** LDAP over validated LDAPS only, Windows Negotiate over Kerberos only, plus release-gated OIDC authorization code + PKCE. Windows loads an authoritative LDAPS snapshot on every login and trusts no PAC groups.
- **Canonical identity:** `(Authority, Subject)`; LDAP and Windows share the AD `objectSid`, OIDC uses `(iss, sub)`. Existing users with the same name are not merged automatically.
- **Server-side authorization:** group memberships come from directory snapshots. The AD sync runs every five minutes by default with 16 concurrent LDAPS lookups (configurable 1–32); snapshots older than 15 minutes are rejected for sessions, jobs and triggers.
- **OIDC freshness:** token groups need an `iat` no older than 15 minutes. SCIM overage memberships need authority-scoped `LastSeenAt` values within the same window; a login or a user PUT does not extend them.
- **Offboarding:** deactivation or a tombstone revokes sessions. A deactivation set locally by an admin is sticky; AD and SCIM `active=true` cannot override it.
- **HA+OIDC:** correlation, nonce and tickets require a shared persistent data-protection key ring, a shared certificate with its private key, and `DataProtection:SharedKeyRing=true`.
- **Health:** `/healthz/ready` deliberately checks only the database. The directory state is separate, at `/healthz/directory`, so that a DC outage does not remove the break-glass path from the load balancer.
- **SignalR authentication:** the httpOnly `np_auth` cookie during the WebSocket upgrade (for `/hubs/` only); no `?access_token=` query string.

Status and operating requirements: [AD SSO Preview](../enterprise/ldap-windows-sso). OIDC/SCIM have a separate release gate; SAML is out of scope.

## Authorization

Roles **Admin / Operator / Viewer**. See [Authentication & roles](../api/authentication). Folder RBAC (stage A) adds per-folder roles — see [Folder RBAC](../enterprise/folder-rbac).

## Output redaction

`OutputRedactor` masks secrets. **Always active.** Custom patterns via `Logging:Redaction:Patterns`.

## Localhost bypass

Without credentials, `runScript` runs in-process. **A product feature; do not introduce a guard against it.**

## Security headers (non-development)

HSTS, CSP, `X-Frame-Options=DENY`, `nosniff`, `Referrer-Policy`.

## External trigger

Gated by `X-Api-Key`; keys are configured as a SHA-256 hash with a GUID-based workflow allow-list. The target workflow additionally has to contain an active `manualTrigger`. The legacy key has no effect without an explicit `AllowedWorkflowIds` list. The highest-priority declared `Keys` map replaces lower-priority providers completely (`{}` revokes all keys), and so do scope lists (`[]` = deny-all). Idempotency replays are isolated per authenticated key principal and persist no raw header value.

## Rate limiting

Per IP, sliding window — see [Authentication](../api/authentication).

## REST API proxy

`RestApi:Proxy:Enabled` (default `false`). Per-step override via `proxyMode` (`default`/`direct`/`custom`).

## LLM proxy

`Llm:Proxy:Mode` (default `Off`) — separate from the REST API proxy, because AI traffic and workflow
traffic are treated differently in corporate networks. `System` adopts the service account's proxy,
`Custom` a dedicated address with a bypass list. If the traffic goes through a proxy, that proxy
resolves the target address — the check immediately before the connection is established then only
applies to the proxy itself, while the base URL is still checked when saving and at startup.
Details: [AI features](../ai-features).

## Hardening flags

The complete list of guard flags with their defaults: [Hardening flags](./hardening).

## Audit log

Inject `IAuditWriter` and call `await _audit.LogAsync(AuditActions.WorkflowPublished, "Workflow", resourceId, detailsJson, ct)` **after** `SaveChanges` — codes **always** as a constant from `NodePilot.Core.Audit.AuditActions`, never as a raw string literal (the guard test `AuditActionsCatalogTests` enforces this). A write failure must never abort the mutation. Passwords/secrets never in the details. Admin only, cursor pagination, export as CSV/NDJSON. Details: [Audit log](./audit-log).

## Secrets

Credentials and secret-flagged globals are encrypted at rest through `ISecretProtector` (DPAPI by default, AES-GCM cluster-portable). Details: [Secret providers](../enterprise/secrets-providers).
