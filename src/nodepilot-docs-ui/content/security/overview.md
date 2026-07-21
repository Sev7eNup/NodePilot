# Sicherheitsmodell

NodePilot ist **hardened by default** — Schutzmechanismen sind an, `appsettings.Development.json` relaxt sie für lokale Iteration auf `false`.

## Auth & Session

- **Serverseitige Sessions:** standardmäßig acht Stunden absolute Lebensdauer, einzeln widerrufbar. JWTs tragen Session-ID, Security-Stamp und `jti`; Gruppen bleiben aus JWT und Cookie heraus.
- **Lokale BCrypt-Passwörter:** Produktionsdefault `BreakGlassOnly`; nur explizit markierte Notfallkonten dürfen sich lokal anmelden.
- **Externe Pfade:** LDAP ausschließlich über validiertes LDAPS, Windows Negotiate ausschließlich Kerberos sowie release-gated OIDC Authorization Code + PKCE. Windows lädt bei jedem Login einen autoritativen LDAPS-Snapshot und vertraut keinen PAC-Gruppen.
- **Kanonische Identität:** `(Authority, Subject)`; LDAP und Windows teilen sich den AD-`objectSid`, OIDC verwendet `(iss, sub)`. Gleichnamige bestehende Benutzer werden nicht automatisch zusammengeführt.
- **Serverseitige Autorisierung:** Gruppen-Memberships kommen aus Directory-Snapshots. AD-Sync läuft standardmäßig alle fünf Minuten mit 16 parallelen LDAPS-Lookups (konfigurierbar 1–32); Snapshots über 15 Minuten werden für Sessions, Jobs und Trigger abgewiesen.
- **OIDC-Freshness:** Token-Gruppen brauchen ein höchstens 15 Minuten altes `iat`. SCIM-Overage-Memberships brauchen authority-scoped `LastSeenAt`-Werte im selben Fenster; Login oder User-PUT verlängern sie nicht.
- **Offboarding:** Deaktivierung oder Tombstone widerruft Sessions. Eine lokal durch einen Admin gesetzte Deaktivierung ist sticky; AD und SCIM `active=true` können sie nicht überschreiben.
- **HA+OIDC:** Correlation, Nonce und Tickets benötigen einen gemeinsamen persistenten Data-Protection-Keyring, ein gemeinsames Zertifikat mit Private Key und `DataProtection:SharedKeyRing=true`.
- **Health:** `/healthz/ready` prüft bewusst nur die DB. Der Directory-Zustand liegt separat unter `/healthz/directory`, damit ein DC-Ausfall den Break-Glass-Pfad nicht aus dem Load Balancer entfernt.
- **SignalR-Auth:** httpOnly `np_auth`-Cookie beim WebSocket-Upgrade (nur `/hubs/`); kein `?access_token=`-Querystring.

Status und Betriebsvorgaben: [AD SSO Preview](../enterprise/ldap-windows-sso). OIDC/SCIM haben ein separates Release-Gate; SAML ist out of scope.

## Authorization

Rollen **Admin / Operator / Viewer**. Siehe [Authentifizierung & Rollen](../api/authentication). Folder-RBAC (Stage A) addiert pro-Folder-Rollen — siehe [Folder-RBAC](../enterprise/folder-rbac).

## Output-Redaction

`OutputRedactor` maskiert Secrets. **Immer aktiv.** Custom-Patterns via `Logging:Redaction:Patterns`.

## Localhost-Bypass

Ohne Credentials läuft `runScript` in-process. **Produkt-Feature, kein Guard einziehen.**

## Security-Headers (Non-Dev)

HSTS, CSP, `X-Frame-Options=DENY`, `nosniff`, `Referrer-Policy`.

## External Trigger

Nur aktiv wenn `ExternalTrigger:ApiKey` gesetzt. Gated via `X-Api-Key`-Header.

## Rate-Limiting

Per-IP, Sliding-Window — siehe [Authentifizierung](../api/authentication).

## REST-API-Proxy

`RestApi:Proxy:Enabled` (default `false`). Per-Step-Override via `proxyMode` (`default`/`direct`/`custom`).

## Hardening-Flags

Die vollständige Liste der Guard-Flags mit Defaults: [Hardening-Flags](./hardening).

## Audit-Log

`IAuditWriter` injizieren, `await _audit.LogAsync(AuditActions.WorkflowPublished, "Workflow", resourceId, detailsJson, ct)` **nach** `SaveChanges` — Codes **immer** als Konstante aus `NodePilot.Core.Audit.AuditActions`, nie als rohes String-Literal (Guard-Test `AuditActionsCatalogTests` erzwingt das). Schreibfehler darf die Mutation nie abbrechen. Passwörter/Secrets nie in Details. Admin-only, Cursor-Pagination, Export CSV/NDJSON. Details: [Audit-Log](./audit-log).

## Secrets

Credentials und secret-flagged Globals werden at-rest verschlüsselt via `ISecretProtector` (DPAPI default, AES-GCM cluster-portable). Details: [Secret-Provider](../enterprise/secrets-providers).
