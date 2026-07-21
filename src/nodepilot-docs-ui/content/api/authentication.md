# Authentifizierung & Rollen

## Session, JWT und BCrypt

- Jeder Login erzeugt eine serverseitig gespeicherte, widerrufbare Session. Die absolute Lebensdauer beträgt standardmäßig acht Stunden; Refresh verlängert diese Grenze nicht.
- Das **JWT** trägt eine opaque Session-ID, Security-Stamp und `jti`, aber keine Gruppenliste. Der Signatur-Key kommt aus `Jwt:Key` oder dem automatisch erzeugten `jwt-secret.key`.
- Lokale Passwörter werden mit **BCrypt** gespeichert. Der Produktionsdefault `Authentication:LocalLoginMode=BreakGlassOnly` erlaubt nur explizit markierte Notfallkonten.
- Alle Loginwege konvergieren auf **JWT-Cookie** (`np_auth`, httpOnly) + **CSRF-Token**. Logout, Deaktivierung, Tombstone oder eine Sicherheitsänderung widerrufen die Session serverseitig.
- Externe Autorisierung muss spätestens alle 15 Minuten aktualisiert sein; veraltete Membership-Snapshots werden abgewiesen. Eine lokal durch einen Admin gesetzte Deaktivierung bleibt sticky und kann weder durch AD noch durch SCIM `active=true` aufgehoben werden.

## Rollen

| Endpoint | Admin | Operator | Viewer |
|---|---|---|---|
| `GET /api/{workflows,executions,machines}` | ✓ | ✓ | ✓ |
| `POST /api/workflows`, `PUT`, `POST /{id}/duplicate|execute` | ✓ | ✓ | ✗ |
| `POST /api/machines`, `PUT` | ✓ | ✓ | ✗ |
| `GET|POST|PUT /api/credentials` | ✓ | ✓ | ✗ |
| `POST /api/executions/{id}/cancel` | ✓ | ✓ | ✗ |
| `DELETE /{workflows,machines,credentials}/{id}` | ✓ | ✗ | ✗ |
| `POST /api/trigger/{name}` | API-Key via `X-Api-Key`-Header | | |

**Initial-Admin:** erster Login bei leerer DB (One-Shot-Token `admin-setup.token`).

## Auth-Pfade

Die aktiven Pfade werden beim Prozessstart eingefroren und erzeugen dieselbe Session:

| Pfad | Endpoint | Default | Use Case |
|---|---|---|---|
| Lokales BCrypt-Passwort | `POST /api/auth/login` | `BreakGlassOnly` | Notfallzugang; optional `Disabled` oder `Enabled` |
| LDAP Simple Bind über LDAPS | `POST /api/auth/login` | aus | Domänen-Login mit Benutzername und Passwort |
| Windows Negotiate/Kerberos | `POST /api/auth/windows` | aus | SSO von domänengebundenen Windows-Clients |
| OpenID Connect | `GET /api/auth/oidc` | aus, release-gated | Authorization Code + PKCE für Enterprise-IdPs |

Details, sichere Defaults und Preview-Status: [AD SSO Preview](../enterprise/ldap-windows-sso).

`GET /api/auth/methods` (anonym) ist das Discovery-Endpoint für die LoginPage:

```json
{
  "local": true,
  "ldap": true,
  "windows": true,
  "windowsEndpoint": "/api/auth/windows",
  "oidc": true,
  "oidcEndpoint": "/api/auth/oidc",
  "oidcDisplayName": "Firmenkonto"
}
```

Deaktivierte Methoden melden `false` und `null`. `local` ist nur bei `LocalLoginMode=Disabled` falsch. Änderungen an `Authentication:*` benötigen einen Service-Neustart. Im Cluster ist die Sektion Config-as-Code; ein Admin-PUT liefert `409 CLUSTER_CONFIG_AS_CODE_REQUIRED`.

## Beispiele

Login setzt das httpOnly `np_auth`-Cookie + ein `np_csrf`-Cookie. Browser-Caller bekommen nur die Identität zurück; der Token **in** der Response-Body fehlt bewusst. Wer den Token als Bearer braucht (CLI, Skripte ohne Cookie-Jar), setzt den Header `X-Auth-Token-Response: true`.

```bash
NP=http://localhost:5000

# Browser-Style: Token nur im Cookie, Body = Identität
curl -s -c cookie.jar -X POST "$NP/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{ "username":"admin", "password":"s3cret-pass" }'
# 200 → {"userId":"...","username":"admin","role":"Admin"}   (kein Token-Feld)

# Skript-Style: Token in der Body (für Authorization: Bearer <token>)
curl -s -c cookie.jar -X POST "$NP/api/auth/login" \
  -H 'Content-Type: application/json' \
  -H 'X-Auth-Token-Response: true' \
  -d '{ "username":"admin", "password":"s3cret-pass" }'
# 200 → {"token":"eyJ...","userId":"...","username":"admin","role":"Admin"}

# Folgende Aufrufe authentifizieren (Cookie oder Bearer)
curl -s -b cookie.jar "$NP/api/auth/me"
# {"id":"...","username":"admin","role":"Admin"}
curl -s -H "Authorization: Bearer $TOKEN" "$NP/api/workflows"
```

**Bootstrap** (leere DB, erster Login): 401 `{"message":"Admin bootstrap required. Send the X-Setup-Token header ..."}`. Setup-Token aus `admin-setup.token`:

```bash
curl -s -c cookie.jar -X POST "$NP/api/auth/login" \
  -H 'Content-Type: application/json' \
  -H 'X-Setup-Token: '"$(cat admin-setup.token)" \
  -H 'X-Auth-Token-Response: true' \
  -d '{ "username":"admin", "password":"new-admin-pass-1" }'
```

LDAP läuft über denselben `POST /api/auth/login` und ist nur mit LDAPS, Service-Bind und Gruppen-Allowlist startfähig. Windows-SSO ist ein separater Negotiate-Endpoint: NodePilot übernimmt die primäre SID aus Kerberos, lädt aber bei jedem Login den autoritativen Account- und Gruppen-Snapshot per Service-Bind über LDAPS. PAC-Gruppen werden nicht für die Autorisierung vertraut. OIDC startet als Browser-Navigation und verwendet keinen Passwort-POST an NodePilot:

```bash
# Windows Negotiate/Kerberos (curl muss SPNEGO können; Windows-Clients nutzen den Browser/HTTP-Client)
curl -s --negotiate -u : -c cookie.jar -X POST "$NP/api/auth/windows"
# 200 → {"userId":"...","username":"DOMAIN\\user","role":"Operator"}   (401/503 wenn nicht konfiguriert)

# OIDC im Browser öffnen; der Provider leitet auf /api/auth/oidc/callback zurück
# Beim IdP als Redirect URI registrieren: $NP/signin-oidc
# /api/auth/oidc/callback ist nur die interne Landing-URL nach der Handler-Validierung.
# GET $NP/api/auth/oidc

# Refresh + Logout
curl -s -b cookie.jar -X POST "$NP/api/auth/refresh" -H 'X-Auth-Token-Response: true'   # neues JWT, gleiche absolute Session-Grenze
curl -s -b cookie.jar -X POST "$NP/api/auth/logout" -i                                   # 204 No Content
```

OIDC-Gruppenclaims werden nur mit vorhandenem, höchstens 15 Minuten altem `iat` akzeptiert. Bei Group-Overage werden ausschließlich frische, authority-scoped SCIM-Memberships verwendet. SCIM-User-`externalId` muss exakt und case-sensitive dem OIDC-`sub` entsprechen; User-Updates erneuern keine Gruppen-Freshness. Ein vollständiger Membership-Snapshot oder Heartbeat mindestens alle 15 Minuten und HA-Failover mit gemeinsamem, zertifikatgeschütztem Data-Protection-Keyring gehören zum Release-Gate. SAML ist out of scope.

> `LoginRequest` hat **kein** `RememberMe`-Feld — nur `Username` + `Password`. 401 bei falschen Credentials: `{"message":"Invalid credentials"}`.

## SignalR-Auth

Der httpOnly `np_auth`-Cookie wird beim WebSocket-Upgrade automatisch mitgeschickt (nur `/hubs/`); **kein** `?access_token=`-Querystring.

## Security-Headers (Non-Dev)

HSTS, CSP, `X-Frame-Options=DENY`, `nosniff`, `Referrer-Policy`.

## Rate-Limiting

Per-IP, Sliding-Window:

| Bereich | Limit |
|---|---|
| login | 50/Min |
| refresh | 20/Min |
| webhook | 60/Min |
| trigger | 30/Min |
| ai-generate | 20/Min |
| audit | 60/Min |
| backup | 10/Min |

## External Trigger

Nur aktiv wenn `ExternalTrigger:ApiKey` gesetzt. Gated via `X-Api-Key`-Header. Akzeptiert optionale `Idempotency-Key`-Header (24 h TTL, nicht abschaltbar).
