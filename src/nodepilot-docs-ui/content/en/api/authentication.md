# Authentication & roles

NodePilot manages revocable server-side sessions. A successful sign-in creates a JWT cookie and a CSRF token. Roles and folder RBAC determine the available actions.

## Sessions, JWT and BCrypt

- Every login creates a server-side, revocable session. The absolute lifetime is eight hours by default; a refresh does not extend that limit.
- The **JWT** carries an opaque session ID, a security stamp and a `jti`, but no group list. The signing key comes from `Jwt:Key` or the automatically generated `jwt-secret.key`.
- Local passwords are stored with **BCrypt**. The production default `Authentication:LocalLoginMode=BreakGlassOnly` permits only explicitly marked emergency accounts.
- All login paths converge on a **JWT cookie** (`np_auth`, httpOnly) + a **CSRF token**. Logout, deactivation, a tombstone or a security change revokes the session server-side.
- External authorization has to be refreshed at least every 15 minutes; stale membership snapshots are rejected. A deactivation set locally by an admin stays sticky and cannot be lifted by AD or by SCIM `active=true`.

## Roles

| Endpoint | Admin | Operator | Viewer |
|---|---|---|---|
| `GET /api/{workflows,executions,machines}` | ✓ | ✓ | ✓ |
| `POST /api/workflows`, `PUT`, `POST /{id}/duplicate|execute` | ✓ | ✓ | ✗ |
| `POST /api/machines`, `PUT` | ✓ | ✓ | ✗ |
| `GET|POST|PUT /api/credentials` | ✓ | ✓ | ✗ |
| `POST /api/executions/{id}/cancel` | ✓ | ✓ | ✗ |
| `DELETE /{workflows,machines,credentials}/{id}` | ✓ | ✗ | ✗ |
| `POST /api/trigger/{name}` | An API key through the `X-Api-Key` header | | |

**The initial admin:** the first sign-in against an empty database (using the one-shot token `admin-setup.token`).

## Authentication paths

The active paths are frozen at process start and all produce the same session:

| Path | Endpoint | Default | Use case |
|---|---|---|---|
| A local BCrypt password | `POST /api/auth/login` | `BreakGlassOnly` | Emergency access; optionally `Disabled` or `Enabled` |
| LDAP simple bind over LDAPS | `POST /api/auth/login` | off | Domain sign-in with a user name and password |
| Windows Negotiate/Kerberos | `POST /api/auth/windows` | off | SSO from domain-joined Windows clients |
| OpenID Connect | `GET /api/auth/oidc` | off, release-gated | Authorization code + PKCE for enterprise IdPs |

Details, safe defaults and preview status: [AD SSO Preview](../enterprise/ldap-windows-sso).

`GET /api/auth/methods` (anonymous) is the discovery endpoint for the login page:

```json
{
  "local": true,
  "ldap": true,
  "windows": true,
  "windowsEndpoint": "/api/auth/windows",
  "oidc": true,
  "oidcEndpoint": "/api/auth/oidc",
  "oidcDisplayName": "Company account"
}
```

Disabled methods report `false` and `null`. `local` is only false with `LocalLoginMode=Disabled`. Changes to `Authentication:*` require a service restart. In a cluster the section is config-as-code; an admin PUT returns `409 CLUSTER_CONFIG_AS_CODE_REQUIRED`.

## Examples

A login sets the httpOnly `np_auth` cookie and an `np_csrf` cookie. Browser clients only receive the identity; the response deliberately contains no token. CLI calls and scripts without a cookie jar request a bearer token with the header `X-Auth-Token-Response: true`.

```bash
NP=http://localhost:5000

# Browser style: the token is in the cookie only, the body is the identity
curl -s -c cookie.jar -X POST "$NP/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{ "username":"admin", "password":"s3cret-pass" }'
# 200 → {"userId":"...","username":"admin","role":"Admin"}   (no token field)

# Script style: the token in the body (for Authorization: Bearer <token>)
curl -s -c cookie.jar -X POST "$NP/api/auth/login" \
  -H 'Content-Type: application/json' \
  -H 'X-Auth-Token-Response: true' \
  -d '{ "username":"admin", "password":"s3cret-pass" }'
# 200 → {"token":"eyJ...","userId":"...","username":"admin","role":"Admin","expiresAt":"2026-08-15T18:30:00Z"}

# Subsequent calls authenticate (cookie or bearer)
curl -s -b cookie.jar "$NP/api/auth/me"
# {"id":"...","username":"admin","role":"Admin"}
curl -s -H "Authorization: Bearer $TOKEN" "$NP/api/workflows"
```

**Bootstrap** (an empty database, the first sign-in): 401 `{"code":"SETUP_TOKEN_REQUIRED","message":"Admin bootstrap required. Send the X-Setup-Token header ..."}` — the web interface reacts to that code and reveals its setup-token field. Through the API, use the setup token from `admin-setup.token`:

```bash
curl -s -c cookie.jar -X POST "$NP/api/auth/login" \
  -H 'Content-Type: application/json' \
  -H 'X-Setup-Token: '"$(cat admin-setup.token)" \
  -H 'X-Auth-Token-Response: true' \
  -d '{ "username":"admin", "password":"new-admin-pass-1" }'
```

LDAP goes through the same `POST /api/auth/login` and only starts with LDAPS, a service bind and a group allow-list. Windows SSO is a separate Negotiate endpoint: NodePilot takes the primary SID from Kerberos but loads the authoritative account and group snapshot through a service bind over LDAPS on every login. PAC groups are not trusted for authorization. OIDC starts as a browser navigation and uses no password POST to NodePilot:

```bash
# Windows Negotiate/Kerberos (curl has to support SPNEGO; Windows clients use the browser/HTTP client)
curl -s --negotiate -u : -c cookie.jar -X POST "$NP/api/auth/windows"
# 200 → {"userId":"...","username":"DOMAIN\\user","role":"Operator"}   (401/503 if not configured)

# Open OIDC in the browser; the provider redirects back to /api/auth/oidc/callback
# Register at the IdP as the redirect URI: $NP/signin-oidc
# /api/auth/oidc/callback is only the internal landing URL after the handler's validation.
# GET $NP/api/auth/oidc

# Refresh + logout
curl -s -b cookie.jar -X POST "$NP/api/auth/refresh" -H 'X-Auth-Token-Response: true'   # a new JWT, the same absolute session limit
curl -s -b cookie.jar -X POST "$NP/api/auth/logout" -i                                   # 204 No Content
```

OIDC group claims are only accepted with a present `iat` no older than 15 minutes. On group overage, only fresh, authority-scoped SCIM memberships are used. A SCIM user's `externalId` has to match the OIDC `sub` exactly and case-sensitively; user updates do not renew group freshness. A complete membership snapshot or heartbeat at least every 15 minutes, and HA failover with a shared, certificate-protected data-protection key ring, are part of the release gate. SAML is out of scope.

> `LoginRequest` has **no** `RememberMe` field — only `Username` + `Password`. 401 on wrong credentials: `{"message":"Invalid credentials"}`.

## SignalR authentication

The httpOnly `np_auth` cookie is sent automatically during the WebSocket upgrade (for `/hubs/` only); there is **no** `?access_token=` query string.

## Security headers (non-development)

HSTS, CSP, `X-Frame-Options=DENY`, `nosniff`, `Referrer-Policy`.

## Rate limiting

For the authentication endpoints, per IP in a sliding window: **login 50/min**, **refresh 20/min**.

The complete table across all areas is — as the single source — under [Hardening flags](../security/hardening).

## External trigger

Gated by `X-Api-Key`. SHA-256-hashed keys under `ExternalTrigger:Keys:<id>` with a GUID-based `AllowedWorkflowIds` list are preferred. The workflow additionally has to contain an active `manualTrigger`. The legacy key `ExternalTrigger:ApiKey` has no effect without its own `AllowedWorkflowIds` list. The highest-priority declared `Keys` map is the complete snapshot (`{}` revokes lower-priority keys); allow-lists are atomic as well (`[]` = deny-all). Optional `Idempotency-Key` headers are valid for 24 hours and are additionally bound to the authenticated key principal; only a domain-separated digest is stored.
